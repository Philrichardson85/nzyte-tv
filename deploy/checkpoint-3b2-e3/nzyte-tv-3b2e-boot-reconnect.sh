#!/usr/bin/env bash

set -u
set -o pipefail

PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH
umask 077

readonly PRODUCTION_SERVICE="${E3_PRODUCTION_SERVICE:-nzyte-tv.service}"
readonly TEST_SERVICE="${E3_TEST_SERVICE:-nzyte-tv-3b2d.service}"
readonly STATE_DIR="${E3_STATE_DIR:-/opt/nzyte-tv/integration/3b2d/state/e3}"
readonly BOOT_ID_SOURCE="${E3_BOOT_ID_FILE:-/proc/sys/kernel/random/boot_id}"
readonly E3_PROCFS_ROOT="${E3_PROC_ROOT:-/proc}"
readonly PROC_ROOT="${E3_PROC_ROOT:-/proc}"
readonly BOOT_ID_MARKER="${STATE_DIR}/last-attempt-boot-id"
readonly OUTCOME_FILE="${STATE_DIR}/last-outcome.env"
readonly LOCK_FILE="${STATE_DIR}/reconnect.lock"
readonly DIAGNOSTICS_FILE="${E3_DIAGNOSTICS_FILE:-/opt/nzyte-tv/integration/3b2d/state/broadcast-diagnostics.json}"
readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
readonly DIAGNOSTICS_READER="${E3_DIAGNOSTICS_READER_BIN:-${SCRIPT_DIR}/nzyte-tv-3b2e-read-diagnostics.py}"

readonly SYSTEMCTL_BIN="${E3_SYSTEMCTL_BIN:-/usr/bin/systemctl}"
readonly PGREP_BIN="${E3_PGREP_BIN:-/usr/bin/pgrep}"
readonly FLOCK_BIN="${E3_FLOCK_BIN:-/usr/bin/flock}"
readonly SLEEP_BIN="${E3_SLEEP_BIN:-/usr/bin/sleep}"
readonly DATE_BIN="${E3_DATE_BIN:-/usr/bin/date}"
readonly PYTHON_BIN="${E3_PYTHON_BIN:-/usr/bin/python3}"

readonly OBSERVATION_SECONDS="${E3_PROGRESS_OBSERVATION_SECONDS:-35}"
readonly MAX_PROGRESS_AGE_SECONDS="${E3_MAX_PROGRESS_AGE_SECONDS:-90}"
readonly STOP_TIMEOUT_SECONDS="${E3_STOP_TIMEOUT_SECONDS:-30}"
readonly START_TIMEOUT_SECONDS="${E3_START_TIMEOUT_SECONDS:-45}"
readonly POLL_SECONDS="${E3_POLL_SECONDS:-1}"

log_safe() {
    printf 'NZYTE TV E3: %s\n' "$1"
}

require_nonnegative_integer() {
    [[ "$1" =~ ^[0-9]+$ ]]
}

read_boot_id() {
    local boot_id
    if [[ ! -r "$BOOT_ID_SOURCE" ]] || ! IFS= read -r boot_id < "$BOOT_ID_SOURCE"; then
        return 1
    fi

    if [[ ! "$boot_id" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
        return 1
    fi

    printf '%s\n' "${boot_id,,}"
}

write_atomic_line() {
    local path="$1"
    local value="$2"
    local temporary_path="${path}.partial.$$"
    if ! printf '%s\n' "$value" > "$temporary_path"; then
        return 1
    fi

    chmod 600 "$temporary_path" 2>/dev/null || true
    if ! mv -f -- "$temporary_path" "$path"; then
        rm -f -- "$temporary_path"
        return 1
    fi
}

record_outcome() {
    local boot_id="$1"
    local result="$2"
    local reason="$3"
    local timestamp
    local temporary_path="${OUTCOME_FILE}.partial.$$"

    timestamp="$($DATE_BIN -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null)" || timestamp="unknown"
    if ! {
        printf 'schemaVersion=1\n'
        printf 'timestampUtc=%s\n' "$timestamp"
        printf 'bootId=%s\n' "$boot_id"
        printf 'result=%s\n' "$result"
        printf 'reason=%s\n' "$reason"
    } > "$temporary_path"; then
        return 1
    fi

    chmod 600 "$temporary_path" 2>/dev/null || true
    if ! mv -f -- "$temporary_path" "$OUTCOME_FILE"; then
        rm -f -- "$temporary_path"
        return 1
    fi
}

skip_boot() {
    local boot_id="$1"
    local reason="$2"
    record_outcome "$boot_id" "skipped" "$reason" || true
    log_safe "skipped (${reason})"
    exit 0
}

fail_action() {
    local boot_id="$1"
    local reason="$2"
    record_outcome "$boot_id" "failed" "$reason" || true
    log_safe "failed (${reason})"
    exit 1
}

service_is_active() {
    "$SYSTEMCTL_BIN" is-active --quiet "$1" >/dev/null 2>&1
}

service_state() {
    local value
    value="$($SYSTEMCTL_BIN is-active "$1" 2>/dev/null)" || true
    if [[ ! "$value" =~ ^(active|inactive|activating|deactivating|failed|reloading)$ ]]; then
        return 1
    fi

    printf '%s\n' "$value"
}

service_enablement() {
    local value
    value="$($SYSTEMCTL_BIN is-enabled "$1" 2>/dev/null)" || true
    if [[ ! "$value" =~ ^(enabled|disabled|masked|static|indirect|generated|transient)$ ]]; then
        return 1
    fi

    printf '%s\n' "$value"
}

service_main_pid() {
    local value
    value="$($SYSTEMCTL_BIN show "$1" --property=MainPID --value 2>/dev/null)" || return 1
    if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
        return 1
    fi

    printf '%s\n' "$value"
}

process_count() {
    local name="$1"
    local value
    value="$($PGREP_BIN -x -c "$name" 2>/dev/null)" || true
    if [[ ! "$value" =~ ^[0-9]+$ ]]; then
        return 1
    fi

    printf '%s\n' "$value"
}

single_process_pid() {
    local name="$1"
    local value
    value="$($PGREP_BIN -x "$name" 2>/dev/null)" || return 1
    if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
        return 1
    fi

    printf '%s\n' "$value"
}

process_parent_pid() {
    local pid="$1"
    local key
    local value
    local remainder
    local status_file="${E3_PROCFS_ROOT}/${pid}/status"
    [[ -r "$status_file" ]] || return 1

    while IFS=$'\t ' read -r key value remainder; do
        if [[ "$key" == "PPid:" ]]; then
            [[ "$value" =~ ^[1-9][0-9]*$ ]] || return 1
            printf '%s\n' "$value"
            return 0
        fi
    done < "$status_file"

    return 1
}

process_parent_pid() {
    local pid="$1"
    local key
    local value
    local remainder
    local status_file="${PROC_ROOT}/${pid}/status"
    [[ -r "$status_file" ]] || return 1

    while IFS=$'\t ' read -r key value remainder; do
        if [[ "$key" == "PPid:" ]]; then
            [[ "$value" =~ ^[1-9][0-9]*$ ]] || return 1
            printf '%s\n' "$value"
            return 0
        fi
    done < "$status_file"

    return 1
}

read_diagnostics() {
    if [[ -n "${E3_DIAGNOSTICS_READER_BIN:-}" ]]; then
        "$DIAGNOSTICS_READER" "$DIAGNOSTICS_FILE" 2>/dev/null
    else
        "$PYTHON_BIN" "$DIAGNOSTICS_READER" "$DIAGNOSTICS_FILE" 2>/dev/null
    fi
}

parse_diagnostics_snapshot() {
    local snapshot="$1"
    local attempt_id
    local ffmpeg_pid
    local output_microseconds
    local last_advancing_epoch
    local extra
    IFS=$'\t' read -r attempt_id ffmpeg_pid output_microseconds last_advancing_epoch extra <<< "$snapshot"

    if [[ ! "$attempt_id" =~ ^[0-9a-fA-F]{32}$ ]] \
        || [[ ! "$ffmpeg_pid" =~ ^[1-9][0-9]*$ ]] \
        || [[ ! "$output_microseconds" =~ ^[0-9]+$ ]] \
        || [[ ! "$last_advancing_epoch" =~ ^[0-9]+$ ]] \
        || [[ -n "${extra:-}" ]]; then
        return 1
    fi

    printf '%s\t%s\t%s\t%s\n' \
        "${attempt_id,,}" "$ffmpeg_pid" "$output_microseconds" "$last_advancing_epoch"
}

progress_is_recent() {
    local last_advancing_epoch="$1"
    local now_epoch
    local age
    now_epoch="$($DATE_BIN -u +%s 2>/dev/null)" || return 1
    if [[ ! "$now_epoch" =~ ^[0-9]+$ ]]; then
        return 1
    fi

    age=$((now_epoch - last_advancing_epoch))
    (( age >= -5 && age <= MAX_PROGRESS_AGE_SECONDS ))
}

wait_for_stopped() {
    local elapsed=0
    local app_count
    local ffmpeg_count
    while (( elapsed <= STOP_TIMEOUT_SECONDS )); do
        app_count="$(process_count nzytetv)" || app_count="invalid"
        ffmpeg_count="$(process_count ffmpeg)" || ffmpeg_count="invalid"
        if ! service_is_active "$TEST_SERVICE" \
            && [[ "$app_count" == "0" ]] \
            && [[ "$ffmpeg_count" == "0" ]]; then
            return 0
        fi

        if (( elapsed == STOP_TIMEOUT_SECONDS )); then
            break
        fi

        "$SLEEP_BIN" "$POLL_SECONDS" >/dev/null 2>&1 || return 1
        elapsed=$((elapsed + POLL_SECONDS))
    done

    return 1
}

wait_for_started() {
    local elapsed=0
    local main_pid
    local app_count
    local ffmpeg_count
    local app_pid
    while (( elapsed <= START_TIMEOUT_SECONDS )); do
        main_pid="$(service_main_pid "$TEST_SERVICE")" || main_pid="invalid"
        app_count="$(process_count nzytetv)" || app_count="invalid"
        ffmpeg_count="$(process_count ffmpeg)" || ffmpeg_count="invalid"
        app_pid="$(single_process_pid nzytetv)" || app_pid="invalid"
        if service_is_active "$TEST_SERVICE" \
            && [[ "$app_count" == "1" ]] \
            && [[ "$ffmpeg_count" == "1" ]] \
            && [[ "$main_pid" == "$app_pid" ]]; then
            return 0
        fi

        if (( elapsed == START_TIMEOUT_SECONDS )); then
            break
        fi

        "$SLEEP_BIN" "$POLL_SECONDS" >/dev/null 2>&1 || return 1
        elapsed=$((elapsed + POLL_SECONDS))
    done

    return 1
}

restore_once() {
    if ! "$SYSTEMCTL_BIN" start "$TEST_SERVICE" >/dev/null 2>&1; then
        return 1
    fi

    wait_for_started
}

validate_numeric_configuration() {
    require_nonnegative_integer "$OBSERVATION_SECONDS" \
        && require_nonnegative_integer "$MAX_PROGRESS_AGE_SECONDS" \
        && require_nonnegative_integer "$STOP_TIMEOUT_SECONDS" \
        && require_nonnegative_integer "$START_TIMEOUT_SECONDS" \
        && require_nonnegative_integer "$POLL_SECONDS" \
        && (( POLL_SECONDS > 0 ))
}

prepare_state_and_lock() {
    if ! mkdir -p -- "$STATE_DIR" || ! chmod 700 "$STATE_DIR"; then
        log_safe "failed (state-directory-unavailable)"
        exit 1
    fi

    exec 9>"$LOCK_FILE" || {
        log_safe "failed (lock-file-unavailable)"
        exit 1
    }
    if ! "$FLOCK_BIN" -n 9 >/dev/null 2>&1; then
        log_safe "skipped (concurrent-invocation)"
        exit 0
    fi
}

seed_current_boot() {
    local boot_id
    prepare_state_and_lock
    boot_id="$(read_boot_id)" || {
        log_safe "failed (boot-id-unavailable)"
        exit 1
    }

    if ! write_atomic_line "$BOOT_ID_MARKER" "$boot_id"; then
        log_safe "failed (boot-marker-write-failed)"
        exit 1
    fi

    record_outcome "$boot_id" "seeded" "current-boot-protected" || {
        log_safe "failed (outcome-write-failed)"
        exit 1
    }
    log_safe "seeded current boot; no service action taken"
}

run_boot_reconnection() {
    local boot_id
    local previous_boot_id=""
    local main_pid
    local app_count
    local app_pid
    local ffmpeg_count
    local ffmpeg_pid
    local ffmpeg_parent_pid
    local ffmpeg_parent_pid
    local first_raw
    local first
    local first_attempt
    local first_diagnostic_pid
    local first_output
    local first_advancing_epoch
    local second_raw
    local second
    local second_attempt
    local second_diagnostic_pid
    local second_output
    local second_advancing_epoch

    prepare_state_and_lock
    if ! validate_numeric_configuration; then
        log_safe "failed (invalid-guard-configuration)"
        exit 1
    fi

    boot_id="$(read_boot_id)" || {
        log_safe "failed (boot-id-unavailable)"
        exit 1
    }

    if [[ -r "$BOOT_ID_MARKER" ]]; then
        IFS= read -r previous_boot_id < "$BOOT_ID_MARKER" || previous_boot_id=""
    fi

    if [[ "$previous_boot_id" == "$boot_id" ]]; then
        log_safe "skipped (boot-already-consumed)"
        exit 0
    fi

    if [[ ! "$previous_boot_id" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
        write_atomic_line "$BOOT_ID_MARKER" "$boot_id" || {
            log_safe "failed (boot-marker-write-failed)"
            exit 1
        }
        skip_boot "$boot_id" "boot-marker-missing-or-invalid-seeded"
    fi

    # Consume this boot before any readiness or service action. A crash, skip, or
    # failed restart therefore cannot turn timer/manual re-entry into a loop.
    write_atomic_line "$BOOT_ID_MARKER" "$boot_id" || {
        log_safe "failed (boot-marker-write-failed)"
        exit 1
    }
    record_outcome "$boot_id" "claimed" "guard-evaluation-started" || {
        log_safe "failed (outcome-write-failed)"
        exit 1
    }

    local production_state
    local production_enablement
    production_state="$(service_state "$PRODUCTION_SERVICE")" \
        || skip_boot "$boot_id" "production-service-state-unknown"
    production_enablement="$(service_enablement "$PRODUCTION_SERVICE")" \
        || skip_boot "$boot_id" "production-service-enablement-unknown"
    if [[ "$production_state" == "active" ]]; then
        skip_boot "$boot_id" "production-service-active"
    fi
    [[ "$production_state" == "inactive" ]] \
        || skip_boot "$boot_id" "production-service-not-inactive"
    [[ "$production_enablement" == "disabled" ]] \
        || skip_boot "$boot_id" "production-service-not-disabled"

    if [[ "$(service_state "$TEST_SERVICE")" != "active" ]]; then
        skip_boot "$boot_id" "test-service-inactive"
    fi

    main_pid="$(service_main_pid "$TEST_SERVICE")" || skip_boot "$boot_id" "test-service-main-pid-invalid"
    app_count="$(process_count nzytetv)" || skip_boot "$boot_id" "nzytetv-process-count-invalid"
    ffmpeg_count="$(process_count ffmpeg)" || skip_boot "$boot_id" "ffmpeg-process-count-invalid"
    [[ "$app_count" == "1" ]] || skip_boot "$boot_id" "nzytetv-process-count-not-one"
    [[ "$ffmpeg_count" == "1" ]] || skip_boot "$boot_id" "ffmpeg-process-count-not-one"

    app_pid="$(single_process_pid nzytetv)" || skip_boot "$boot_id" "nzytetv-pid-unavailable"
    ffmpeg_pid="$(single_process_pid ffmpeg)" || skip_boot "$boot_id" "ffmpeg-pid-unavailable"
    [[ "$main_pid" == "$app_pid" ]] || skip_boot "$boot_id" "service-process-mismatch"
    ffmpeg_parent_pid="$(process_parent_pid "$ffmpeg_pid")" \
        || skip_boot "$boot_id" "ffmpeg-parent-pid-unavailable"
    [[ "$ffmpeg_parent_pid" == "$app_pid" ]] \
        || skip_boot "$boot_id" "ffmpeg-service-process-mismatch"
    ffmpeg_parent_pid="$(process_parent_pid "$ffmpeg_pid")" \
        || skip_boot "$boot_id" "ffmpeg-parent-pid-unavailable"
    [[ "$ffmpeg_parent_pid" == "$app_pid" ]] \
        || skip_boot "$boot_id" "ffmpeg-service-process-mismatch"

    [[ -s "$DIAGNOSTICS_FILE" ]] || skip_boot "$boot_id" "diagnostics-missing"
    first_raw="$(read_diagnostics)" || skip_boot "$boot_id" "diagnostics-invalid"
    first="$(parse_diagnostics_snapshot "$first_raw")" || skip_boot "$boot_id" "diagnostics-invalid"
    IFS=$'\t' read -r first_attempt first_diagnostic_pid first_output first_advancing_epoch <<< "$first"
    [[ "$first_diagnostic_pid" == "$ffmpeg_pid" ]] || skip_boot "$boot_id" "diagnostics-process-mismatch"
    progress_is_recent "$first_advancing_epoch" || skip_boot "$boot_id" "progress-not-recent"

    "$SLEEP_BIN" "$OBSERVATION_SECONDS" >/dev/null 2>&1 \
        || skip_boot "$boot_id" "progress-observation-failed"

    if ! service_is_active "$TEST_SERVICE"; then
        skip_boot "$boot_id" "test-service-changed-during-observation"
    fi

    [[ "$(process_count nzytetv)" == "1" ]] \
        || skip_boot "$boot_id" "nzytetv-changed-during-observation"
    [[ "$(process_count ffmpeg)" == "1" ]] \
        || skip_boot "$boot_id" "ffmpeg-changed-during-observation"
    [[ "$(service_main_pid "$TEST_SERVICE")" == "$main_pid" ]] \
        || skip_boot "$boot_id" "service-process-changed-during-observation"
    [[ "$(single_process_pid nzytetv)" == "$app_pid" ]] \
        || skip_boot "$boot_id" "nzytetv-changed-during-observation"
    [[ "$(single_process_pid ffmpeg)" == "$ffmpeg_pid" ]] \
        || skip_boot "$boot_id" "ffmpeg-changed-during-observation"

    second_raw="$(read_diagnostics)" || skip_boot "$boot_id" "diagnostics-invalid-after-observation"
    second="$(parse_diagnostics_snapshot "$second_raw")" \
        || skip_boot "$boot_id" "diagnostics-invalid-after-observation"
    IFS=$'\t' read -r second_attempt second_diagnostic_pid second_output second_advancing_epoch <<< "$second"
    [[ "$second_attempt" == "$first_attempt" ]] \
        || skip_boot "$boot_id" "diagnostic-attempt-changed"
    [[ "$second_diagnostic_pid" == "$ffmpeg_pid" ]] \
        || skip_boot "$boot_id" "diagnostics-process-mismatch-after-observation"
    (( second_output > first_output )) || skip_boot "$boot_id" "progress-not-advancing"
    (( second_advancing_epoch > first_advancing_epoch )) \
        || skip_boot "$boot_id" "progress-timestamp-not-advancing"
    progress_is_recent "$second_advancing_epoch" || skip_boot "$boot_id" "progress-not-recent-after-observation"

    record_outcome "$boot_id" "attempting" "controlled-reconnection-started" || {
        log_safe "failed (outcome-write-failed)"
        exit 1
    }
    if ! "$SYSTEMCTL_BIN" stop "$TEST_SERVICE" >/dev/null 2>&1; then
        if wait_for_started; then
            fail_action "$boot_id" "stop-command-failed-service-still-active"
        fi
        if restore_once; then
            fail_action "$boot_id" "stop-command-failed-service-restored"
        fi
        fail_action "$boot_id" "stop-command-failed-restore-failed"
    fi

    if ! wait_for_stopped; then
        if restore_once; then
            fail_action "$boot_id" "stop-timeout-service-restored"
        fi
        fail_action "$boot_id" "stop-timeout-restore-failed"
    fi

    if ! "$SYSTEMCTL_BIN" start "$TEST_SERVICE" >/dev/null 2>&1; then
        if restore_once; then
            fail_action "$boot_id" "start-command-failed-service-restored"
        fi
        fail_action "$boot_id" "start-command-failed-restore-failed"
    fi

    if ! wait_for_started; then
        if restore_once; then
            fail_action "$boot_id" "start-timeout-service-restored"
        fi
        fail_action "$boot_id" "start-timeout-restore-failed"
    fi

    record_outcome "$boot_id" "success" "guarded-reconnection-completed" || {
        log_safe "failed (outcome-write-failed-after-reconnection)"
        exit 1
    }
    log_safe "guarded test-service reconnection completed"
}

case "${1:-}" in
    seed)
        seed_current_boot
        ;;
    run)
        run_boot_reconnection
        ;;
    *)
        printf 'Usage: %s {seed|run}\n' "${0##*/}" >&2
        exit 64
        ;;
esac
