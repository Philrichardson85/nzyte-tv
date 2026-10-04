#!/usr/bin/env bash

set -u
set -o pipefail

PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH
umask 077

readonly PRODUCTION_SERVICE="${F_PRODUCTION_SERVICE:-nzyte-tv.service}"
readonly TEST_SERVICE="${F_TEST_SERVICE:-nzyte-tv-3b2d.service}"
readonly PRODUCTION_RECOVERY_TIMER="${F_PRODUCTION_RECOVERY_TIMER:-nzyte-tv-boot-reconnect.timer}"
readonly TEST_RECOVERY_TIMER="${F_TEST_RECOVERY_TIMER:-nzyte-tv-3b2e-boot-reconnect.timer}"
readonly PRODUCTION_RECOVERY_SERVICE="${F_PRODUCTION_RECOVERY_SERVICE:-nzyte-tv-boot-reconnect.service}"
readonly TEST_RECOVERY_SERVICE="${F_TEST_RECOVERY_SERVICE:-nzyte-tv-3b2e-boot-reconnect.service}"
readonly PROCFS_ROOT="${F_PROC_ROOT:-/proc}"
readonly LOCK_FILE="${F_LOCK_FILE:-/run/lock/nzyte-tv-production-handoff.lock}"

readonly SYSTEMCTL_BIN="${F_SYSTEMCTL_BIN:-/usr/bin/systemctl}"
readonly PGREP_BIN="${F_PGREP_BIN:-/usr/bin/pgrep}"
readonly FLOCK_BIN="${F_FLOCK_BIN:-/usr/bin/flock}"
readonly SLEEP_BIN="${F_SLEEP_BIN:-/usr/bin/sleep}"
readonly STOP_TIMEOUT_SECONDS="${F_STOP_TIMEOUT_SECONDS:-45}"
readonly START_TIMEOUT_SECONDS="${F_START_TIMEOUT_SECONDS:-60}"
readonly POLL_SECONDS="${F_POLL_SECONDS:-1}"

log_safe() {
    printf 'NZYTE TV production handoff: %s\n' "$1"
}

require_positive_integer() {
    [[ "$1" =~ ^[1-9][0-9]*$ ]]
}

service_state() {
    local value
    value="$($SYSTEMCTL_BIN is-active "$1" 2>/dev/null)" || true
    [[ "$value" =~ ^(active|inactive|activating|deactivating|failed|reloading)$ ]] || return 1
    printf '%s\n' "$value"
}

service_enablement() {
    local value
    value="$($SYSTEMCTL_BIN is-enabled "$1" 2>/dev/null)" || true
    [[ "$value" =~ ^(enabled|disabled|masked|static|indirect|generated|transient)$ ]] || return 1
    printf '%s\n' "$value"
}

service_main_pid() {
    local value
    value="$($SYSTEMCTL_BIN show "$1" --property=MainPID --value 2>/dev/null)" || return 1
    [[ "$value" =~ ^[1-9][0-9]*$ ]] || return 1
    printf '%s\n' "$value"
}

process_count() {
    local value
    value="$($PGREP_BIN -x -c "$1" 2>/dev/null)" || true
    [[ "$value" =~ ^[0-9]+$ ]] || return 1
    printf '%s\n' "$value"
}

single_process_pid() {
    local value
    value="$($PGREP_BIN -x "$1" 2>/dev/null)" || return 1
    [[ "$value" =~ ^[1-9][0-9]*$ ]] || return 1
    printf '%s\n' "$value"
}

process_parent_pid() {
    local pid="$1"
    local key
    local value
    local remainder
    local status_file="${PROCFS_ROOT}/${pid}/status"
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

exact_broadcaster_belongs_to() {
    local service="$1"
    local main_pid
    local app_pid
    local ffmpeg_pid
    local parent_pid
    [[ "$(service_state "$service")" == "active" ]] || return 1
    [[ "$(process_count nzytetv)" == "1" ]] || return 1
    [[ "$(process_count ffmpeg)" == "1" ]] || return 1
    main_pid="$(service_main_pid "$service")" || return 1
    app_pid="$(single_process_pid nzytetv)" || return 1
    ffmpeg_pid="$(single_process_pid ffmpeg)" || return 1
    parent_pid="$(process_parent_pid "$ffmpeg_pid")" || return 1
    [[ "$main_pid" == "$app_pid" && "$parent_pid" == "$app_pid" ]]
}

wait_for_no_broadcaster() {
    local elapsed=0
    while (( elapsed <= STOP_TIMEOUT_SECONDS )); do
        if [[ "$(process_count nzytetv)" == "0" ]] \
            && [[ "$(process_count ffmpeg)" == "0" ]]; then
            return 0
        fi
        (( elapsed == STOP_TIMEOUT_SECONDS )) && break
        "$SLEEP_BIN" "$POLL_SECONDS" >/dev/null 2>&1 || return 1
        elapsed=$((elapsed + POLL_SECONDS))
    done
    return 1
}

wait_for_broadcaster() {
    local service="$1"
    local elapsed=0
    while (( elapsed <= START_TIMEOUT_SECONDS )); do
        exact_broadcaster_belongs_to "$service" && return 0
        (( elapsed == START_TIMEOUT_SECONDS )) && break
        "$SLEEP_BIN" "$POLL_SECONDS" >/dev/null 2>&1 || return 1
        elapsed=$((elapsed + POLL_SECONDS))
    done
    return 1
}

require_inactive_disabled() {
    [[ "$(service_state "$1")" == "inactive" ]] \
        && [[ "$(service_enablement "$1")" == "disabled" ]]
}

disable_unit() {
    "$SYSTEMCTL_BIN" disable --now "$1" >/dev/null 2>&1 || return 1
    [[ "$(service_enablement "$1")" == "disabled" ]] \
        && [[ "$(service_state "$1")" == "inactive" ]]
}

fail() {
    log_safe "failed ($1)"
    exit 1
}

prepare() {
    require_positive_integer "$STOP_TIMEOUT_SECONDS" \
        && require_positive_integer "$START_TIMEOUT_SECONDS" \
        && require_positive_integer "$POLL_SECONDS" \
        || fail "invalid-timeout-configuration"

    exec 9>"$LOCK_FILE" || fail "lock-file-unavailable"
    "$FLOCK_BIN" -n 9 >/dev/null 2>&1 || fail "concurrent-operation"
}

cleanup_failed_production_start() {
    "$SYSTEMCTL_BIN" disable --now "$PRODUCTION_SERVICE" >/dev/null 2>&1 || true
    wait_for_no_broadcaster
}

cutover() {
    require_inactive_disabled "$PRODUCTION_SERVICE" \
        || fail "production-service-not-inactive-disabled"
    [[ "$(service_enablement "$PRODUCTION_RECOVERY_TIMER")" == "disabled" ]] \
        || fail "production-recovery-timer-not-disabled"
    exact_broadcaster_belongs_to "$TEST_SERVICE" \
        || fail "test-broadcaster-ownership-invalid"

    disable_unit "$TEST_RECOVERY_TIMER" || fail "test-recovery-disable-failed"
    [[ "$(service_state "$TEST_RECOVERY_SERVICE")" == "inactive" ]] \
        || fail "test-recovery-action-in-progress"
    "$SYSTEMCTL_BIN" disable --now "$TEST_SERVICE" >/dev/null 2>&1 \
        || fail "test-service-stop-disable-failed"
    require_inactive_disabled "$TEST_SERVICE" \
        || fail "test-service-not-inactive-disabled"
    wait_for_no_broadcaster || fail "test-process-exit-timeout"

    if ! "$SYSTEMCTL_BIN" enable --now "$PRODUCTION_SERVICE" >/dev/null 2>&1; then
        cleanup_failed_production_start || true
        fail "production-service-enable-start-failed"
    fi
    if ! wait_for_broadcaster "$PRODUCTION_SERVICE"; then
        cleanup_failed_production_start || true
        fail "production-broadcaster-start-timeout"
    fi

    log_safe "production broadcaster started; production recovery remains disabled"
}

rollback_stop_production() {
    disable_unit "$PRODUCTION_RECOVERY_TIMER" \
        || fail "production-recovery-disable-failed"
    [[ "$(service_state "$PRODUCTION_RECOVERY_SERVICE")" == "inactive" ]] \
        || fail "production-recovery-action-in-progress"
    "$SYSTEMCTL_BIN" disable --now "$PRODUCTION_SERVICE" >/dev/null 2>&1 \
        || fail "production-service-stop-disable-failed"
    require_inactive_disabled "$PRODUCTION_SERVICE" \
        || fail "production-service-not-inactive-disabled"
    wait_for_no_broadcaster || fail "production-process-exit-timeout"
    log_safe "production stopped; restore files and state before starting test"
}

rollback_start_test() {
    require_inactive_disabled "$PRODUCTION_SERVICE" \
        || fail "production-service-not-inactive-disabled"
    [[ "$(service_enablement "$PRODUCTION_RECOVERY_TIMER")" == "disabled" ]] \
        || fail "production-recovery-timer-not-disabled"
    [[ "$(service_state "$PRODUCTION_RECOVERY_SERVICE")" == "inactive" ]] \
        || fail "production-recovery-action-in-progress"
    wait_for_no_broadcaster || fail "broadcaster-processes-still-present"
    require_inactive_disabled "$TEST_SERVICE" \
        || fail "test-service-not-inactive-disabled"

    if ! "$SYSTEMCTL_BIN" enable --now "$TEST_SERVICE" >/dev/null 2>&1; then
        "$SYSTEMCTL_BIN" disable --now "$TEST_SERVICE" >/dev/null 2>&1 || true
        fail "test-service-enable-start-failed"
    fi
    if ! wait_for_broadcaster "$TEST_SERVICE"; then
        "$SYSTEMCTL_BIN" disable --now "$TEST_SERVICE" >/dev/null 2>&1 || true
        wait_for_no_broadcaster || true
        fail "test-broadcaster-start-timeout"
    fi
    "$SYSTEMCTL_BIN" enable --now "$TEST_RECOVERY_TIMER" >/dev/null 2>&1 \
        || fail "test-recovery-enable-failed"
    [[ "$(service_enablement "$TEST_RECOVERY_TIMER")" == "enabled" ]] \
        || fail "test-recovery-not-enabled"
    log_safe "accepted test broadcaster and recovery timer restored"
}

prepare
case "${1:-}" in
    cutover) cutover ;;
    rollback-stop-production) rollback_stop_production ;;
    rollback-start-test) rollback_start_test ;;
    *)
        printf 'Usage: %s {cutover|rollback-stop-production|rollback-start-test}\n' \
            "${0##*/}" >&2
        exit 64
        ;;
esac
