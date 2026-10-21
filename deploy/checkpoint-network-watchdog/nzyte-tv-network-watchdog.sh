#!/usr/bin/env bash

set -u
set -o pipefail

PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH
umask 077

readonly STATE_DIR="${NW_STATE_DIR:-/var/lib/nzyte-tv/network-watchdog}"
readonly CONNECTION_NAME="${NW_CONNECTION_NAME:-g-route}"
readonly INTERFACE="${NW_INTERFACE:-wlan0}"
readonly DNS_NAME="${NW_DNS_NAME:-a.rtmp.youtube.com}"

readonly FAILURE_THRESHOLD="${NW_FAILURE_THRESHOLD:-3}"
readonly VERIFY_ATTEMPTS="${NW_VERIFY_ATTEMPTS:-3}"
readonly VERIFY_DELAY_SECONDS="${NW_VERIFY_DELAY_SECONDS:-5}"
readonly PING_TIMEOUT_SECONDS="${NW_PING_TIMEOUT_SECONDS:-2}"
readonly DNS_TIMEOUT_SECONDS="${NW_DNS_TIMEOUT_SECONDS:-8}"
readonly MIN_UPTIME_SECONDS="${NW_MIN_UPTIME_SECONDS:-900}"
readonly REBOOT_COOLDOWN_SECONDS="${NW_REBOOT_COOLDOWN_SECONDS:-21600}"
readonly HEALTHY_REARM_THRESHOLD="${NW_HEALTHY_REARM_THRESHOLD:-5}"

readonly IP_BIN="${NW_IP_BIN:-/usr/sbin/ip}"
readonly PING_BIN="${NW_PING_BIN:-/usr/bin/ping}"
readonly GETENT_BIN="${NW_GETENT_BIN:-/usr/bin/getent}"
readonly NMCLI_BIN="${NW_NMCLI_BIN:-/usr/bin/nmcli}"
readonly SYSTEMCTL_BIN="${NW_SYSTEMCTL_BIN:-/usr/bin/systemctl}"
readonly FLOCK_BIN="${NW_FLOCK_BIN:-/usr/bin/flock}"
readonly SLEEP_BIN="${NW_SLEEP_BIN:-/usr/bin/sleep}"
readonly DATE_BIN="${NW_DATE_BIN:-/usr/bin/date}"
readonly TIMEOUT_BIN="${NW_TIMEOUT_BIN:-/usr/bin/timeout}"

readonly UPTIME_FILE="${NW_UPTIME_FILE:-/proc/uptime}"

readonly FAILURE_FILE="${STATE_DIR}/gateway-failures"
readonly STAGE_FILE="${STATE_DIR}/recovery-stage"
readonly REBOOT_FILE="${STATE_DIR}/last-watchdog-reboot-epoch"
readonly REBOOT_CONSUMED_FILE="${STATE_DIR}/reboot-consumed"
readonly HEALTHY_STREAK_FILE="${STATE_DIR}/healthy-streak"
readonly OUTCOME_FILE="${STATE_DIR}/last-outcome.env"
readonly LOCK_FILE="${STATE_DIR}/watchdog.lock"

log_safe() {
    printf 'NZYTE TV network watchdog: %s\n' "$1"
}

require_nonnegative_integer() {
    [[ "$1" =~ ^[0-9]+$ ]]
}

require_positive_integer() {
    require_nonnegative_integer "$1" && (( "$1" > 0 ))
}

validate_configuration() {
    require_positive_integer "$FAILURE_THRESHOLD" \
        && require_positive_integer "$VERIFY_ATTEMPTS" \
        && require_nonnegative_integer "$VERIFY_DELAY_SECONDS" \
        && require_positive_integer "$PING_TIMEOUT_SECONDS" \
        && require_positive_integer "$DNS_TIMEOUT_SECONDS" \
        && require_nonnegative_integer "$MIN_UPTIME_SECONDS" \
        && require_nonnegative_integer "$REBOOT_COOLDOWN_SECONDS" \
        && require_positive_integer "$HEALTHY_REARM_THRESHOLD" \
        && [[ "$INTERFACE" =~ ^[A-Za-z0-9_.:-]+$ ]] \
        && [[ "$CONNECTION_NAME" =~ ^[A-Za-z0-9_.:-]+$ ]] \
        && [[ "$DNS_NAME" =~ ^[A-Za-z0-9.-]+$ ]]
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

read_integer_or_default() {
    local path="$1"
    local default_value="$2"
    local value

    if [[ ! -e "$path" ]]; then
        printf '%s\n' "$default_value"
        return 0
    fi

    if [[ ! -r "$path" ]] || ! IFS= read -r value < "$path"; then
        return 1
    fi

    require_nonnegative_integer "$value" || return 1
    printf '%s\n' "$value"
}

now_epoch() {
    local value
    value="$("$DATE_BIN" -u +%s 2>/dev/null)" || return 1
    require_nonnegative_integer "$value" || return 1
    printf '%s\n' "$value"
}

record_outcome() {
    local result="$1"
    local reason="$2"
    local failures="$3"
    local stage="$4"
    local gateway="$5"

    local timestamp
    local temporary_path="${OUTCOME_FILE}.partial.$$"

    timestamp="$("$DATE_BIN" -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null)" \
        || timestamp="unknown"

    if [[ ! "$result" =~ ^[a-z-]+$ ]] \
        || [[ ! "$reason" =~ ^[a-z0-9-]+$ ]] \
        || ! require_nonnegative_integer "$failures" \
        || ! require_nonnegative_integer "$stage"; then
        return 1
    fi

    if [[ "$gateway" != "unavailable" ]] \
        && [[ ! "$gateway" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then
        gateway="unavailable"
    fi

    if ! {
        printf 'schemaVersion=1\n'
        printf 'timestampUtc=%s\n' "$timestamp"
        printf 'result=%s\n' "$result"
        printf 'reason=%s\n' "$reason"
        printf 'gatewayFailures=%s\n' "$failures"
        printf 'recoveryStage=%s\n' "$stage"
        printf 'gateway=%s\n' "$gateway"
        printf 'interface=%s\n' "$INTERFACE"
        printf 'connection=%s\n' "$CONNECTION_NAME"
    } > "$temporary_path"; then
        return 1
    fi

    chmod 600 "$temporary_path" 2>/dev/null || true

    if ! mv -f -- "$temporary_path" "$OUTCOME_FILE"; then
        rm -f -- "$temporary_path"
        return 1
    fi
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

discover_gateway() {
    local first
    local second
    local third
    local remainder

    while IFS=' ' read -r first second third remainder; do
        if [[ "$first" == "default" ]] \
            && [[ "$second" == "via" ]] \
            && [[ "$third" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then
            printf '%s\n' "$third"
            return 0
        fi
    done < <("$IP_BIN" -4 route show default dev "$INTERFACE" 2>/dev/null)

    return 1
}

probe_gateway() {
    local gateway="$1"

    "$PING_BIN" \
        -n \
        -c 1 \
        -W "$PING_TIMEOUT_SECONDS" \
        "$gateway" \
        >/dev/null 2>&1
}

probe_dns() {
    "$TIMEOUT_BIN" "$DNS_TIMEOUT_SECONDS" \
        "$GETENT_BIN" ahostsv4 "$DNS_NAME" \
        >/dev/null 2>&1
}

verify_gateway_after_recovery() {
    local attempt
    local gateway

    for (( attempt = 1; attempt <= VERIFY_ATTEMPTS; attempt++ )); do
        gateway="$(discover_gateway)" || gateway=""

        if [[ -n "$gateway" ]] && probe_gateway "$gateway"; then
            printf '%s\n' "$gateway"
            return 0
        fi

        if (( attempt < VERIFY_ATTEMPTS )); then
            "$SLEEP_BIN" "$VERIFY_DELAY_SECONDS" >/dev/null 2>&1 || return 1
        fi
    done

    return 1
}

reset_health_state() {
    local reboot_consumed
    local healthy_streak

    reboot_consumed="$(
        read_integer_or_default "$REBOOT_CONSUMED_FILE" "0"
    )" || return 1

    (( reboot_consumed <= 1 )) || return 1

    write_atomic_line "$FAILURE_FILE" "0" || return 1
    write_atomic_line "$STAGE_FILE" "0" || return 1

    if (( reboot_consumed == 0 )); then
        write_atomic_line "$HEALTHY_STREAK_FILE" "0"
        return
    fi

    healthy_streak="$(
        read_integer_or_default "$HEALTHY_STREAK_FILE" "0"
    )" || return 1

    healthy_streak=$((healthy_streak + 1))

    if (( healthy_streak >= HEALTHY_REARM_THRESHOLD )); then
        # Clear the streak first. If clearing the latch then fails, the
        # fail-safe state remains reboot-consumed.
        write_atomic_line "$HEALTHY_STREAK_FILE" "0" || return 1
        write_atomic_line "$REBOOT_CONSUMED_FILE" "0" || return 1
        return
    fi

    write_atomic_line "$HEALTHY_STREAK_FILE" "$healthy_streak"
}

cycle_connection() {
    "$TIMEOUT_BIN" 25 \
        "$NMCLI_BIN" --wait 20 connection down "$CONNECTION_NAME" \
        >/dev/null 2>&1 || true

    "$SLEEP_BIN" 2 >/dev/null 2>&1 || return 1

    "$TIMEOUT_BIN" 30 \
        "$NMCLI_BIN" --wait 25 connection up "$CONNECTION_NAME" ifname "$INTERFACE" \
        >/dev/null 2>&1
}

cycle_wifi_radio() {
    "$TIMEOUT_BIN" 15 \
        "$NMCLI_BIN" radio wifi off \
        >/dev/null 2>&1 || return 1

    "$SLEEP_BIN" 3 >/dev/null 2>&1 || return 1

    "$TIMEOUT_BIN" 15 \
        "$NMCLI_BIN" radio wifi on \
        >/dev/null 2>&1 || return 1

    "$SLEEP_BIN" 3 >/dev/null 2>&1 || return 1

    "$TIMEOUT_BIN" 30 \
        "$NMCLI_BIN" --wait 25 connection up "$CONNECTION_NAME" ifname "$INTERFACE" \
        >/dev/null 2>&1
}

read_uptime_seconds() {
    local raw
    local ignored

    [[ -r "$UPTIME_FILE" ]] || return 1
    IFS=' ' read -r raw ignored < "$UPTIME_FILE" || return 1

    [[ "$raw" =~ ^[0-9]+([.][0-9]+)?$ ]] || return 1

    printf '%s\n' "${raw%%.*}"
}

run_watchdog() {
    local failures
    local stage
    local gateway="unavailable"
    local recovered_gateway
    local uptime_seconds
    local current_epoch
    local last_reboot_epoch
    local elapsed_since_reboot
    local reboot_consumed

    prepare_state_and_lock

    if (( EUID != 0 )); then
        log_safe "failed (root-required)"
        exit 1
    fi

    if ! validate_configuration; then
        log_safe "failed (invalid-configuration)"
        exit 1
    fi

    failures="$(read_integer_or_default "$FAILURE_FILE" "0")" || {
        log_safe "failed (invalid-failure-state)"
        exit 1
    }

    stage="$(read_integer_or_default "$STAGE_FILE" "0")" || {
        log_safe "failed (invalid-recovery-stage)"
        exit 1
    }

    if (( stage > 2 )); then
        log_safe "failed (unsupported-recovery-stage)"
        exit 1
    fi

    gateway="$(discover_gateway)" || gateway="unavailable"

    if [[ "$gateway" != "unavailable" ]] && probe_gateway "$gateway"; then
        reset_health_state || {
            log_safe "failed (health-state-reset-failed)"
            exit 1
        }

        if probe_dns; then
            record_outcome "healthy" "gateway-and-dns-reachable" "0" "0" "$gateway" || true
            log_safe "healthy"
        else
            record_outcome "degraded" "dns-unresolved-gateway-healthy" "0" "0" "$gateway" || true
            log_safe "degraded (dns-unresolved-gateway-healthy)"
        fi

        exit 0
    fi

    write_atomic_line "$HEALTHY_STREAK_FILE" "0" || {
        log_safe "failed (healthy-streak-reset-failed)"
        exit 1
    }

    failures=$((failures + 1))

    write_atomic_line "$FAILURE_FILE" "$failures" || {
        log_safe "failed (failure-state-write-failed)"
        exit 1
    }

    if (( failures < FAILURE_THRESHOLD )); then
        record_outcome "degraded" "gateway-unreachable" "$failures" "$stage" "$gateway" || true
        log_safe "degraded (gateway-unreachable)"
        exit 0
    fi

    if (( stage == 0 )); then
        record_outcome "recovering" "connection-cycle-started" "$failures" "$stage" "$gateway" || true
        log_safe "recovery (connection-cycle)"

        cycle_connection || true

        recovered_gateway="$(verify_gateway_after_recovery)" || recovered_gateway=""

        if [[ -n "$recovered_gateway" ]]; then
            reset_health_state || {
                log_safe "failed (health-state-reset-failed)"
                exit 1
            }

            record_outcome "recovered" "connection-cycle-succeeded" "0" "0" "$recovered_gateway" || true
            log_safe "recovered (connection-cycle-succeeded)"
            exit 0
        fi

        write_atomic_line "$FAILURE_FILE" "0" || exit 1
        write_atomic_line "$STAGE_FILE" "1" || exit 1

        record_outcome "degraded" "connection-cycle-failed" "0" "1" "unavailable" || true
        log_safe "degraded (connection-cycle-failed)"
        exit 0
    fi

    if (( stage == 1 )); then
        record_outcome "recovering" "wifi-radio-cycle-started" "$failures" "$stage" "$gateway" || true
        log_safe "recovery (wifi-radio-cycle)"

        cycle_wifi_radio || true

        recovered_gateway="$(verify_gateway_after_recovery)" || recovered_gateway=""

        if [[ -n "$recovered_gateway" ]]; then
            reset_health_state || {
                log_safe "failed (health-state-reset-failed)"
                exit 1
            }

            record_outcome "recovered" "wifi-radio-cycle-succeeded" "0" "0" "$recovered_gateway" || true
            log_safe "recovered (wifi-radio-cycle-succeeded)"
            exit 0
        fi

        write_atomic_line "$FAILURE_FILE" "0" || exit 1
        write_atomic_line "$STAGE_FILE" "2" || exit 1

        record_outcome "degraded" "wifi-radio-cycle-failed" "0" "2" "unavailable" || true
        log_safe "degraded (wifi-radio-cycle-failed)"
        exit 0
    fi

    reboot_consumed="$(
        read_integer_or_default "$REBOOT_CONSUMED_FILE" "0"
    )" || {
        record_outcome "degraded" "reboot-suppressed-invalid-incident-state" "$failures" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-invalid-incident-state)"
        exit 0
    }

    if (( reboot_consumed > 1 )); then
        record_outcome "degraded" "reboot-suppressed-invalid-incident-state" "$failures" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-invalid-incident-state)"
        exit 0
    fi

    if (( reboot_consumed == 1 )); then
        write_atomic_line "$FAILURE_FILE" "0" || exit 1
        record_outcome "degraded" "reboot-suppressed-incident-consumed" "0" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-incident-consumed)"
        exit 0
    fi

    uptime_seconds="$(read_uptime_seconds)" || {
        record_outcome "degraded" "reboot-suppressed-uptime-unavailable" "$failures" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-uptime-unavailable)"
        exit 0
    }

    if (( uptime_seconds < MIN_UPTIME_SECONDS )); then
        write_atomic_line "$FAILURE_FILE" "0" || exit 1
        record_outcome "degraded" "reboot-suppressed-minimum-uptime" "0" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-minimum-uptime)"
        exit 0
    fi

    current_epoch="$(now_epoch)" || {
        record_outcome "degraded" "reboot-suppressed-clock-unavailable" "$failures" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-clock-unavailable)"
        exit 0
    }

    last_reboot_epoch="$(read_integer_or_default "$REBOOT_FILE" "0")" || {
        record_outcome "degraded" "reboot-suppressed-invalid-history" "$failures" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-invalid-history)"
        exit 0
    }

    if (( last_reboot_epoch > current_epoch )); then
        write_atomic_line "$FAILURE_FILE" "0" || exit 1
        record_outcome "degraded" "reboot-suppressed-clock-regression" "0" "$stage" "$gateway" || true
        log_safe "degraded (reboot-suppressed-clock-regression)"
        exit 0
    fi

    if (( last_reboot_epoch > 0 )); then
        elapsed_since_reboot=$((current_epoch - last_reboot_epoch))

        if (( elapsed_since_reboot < REBOOT_COOLDOWN_SECONDS )); then
            write_atomic_line "$FAILURE_FILE" "0" || exit 1
            record_outcome "degraded" "reboot-suppressed-cooldown" "0" "$stage" "$gateway" || true
            log_safe "degraded (reboot-suppressed-cooldown)"
            exit 0
        fi
    fi

    # Consume the reboot allowance before recording history or asking systemd
    # to reboot. Any later failure therefore remains fail-safe and cannot
    # create a reboot loop for the same unresolved network incident.
    write_atomic_line "$REBOOT_CONSUMED_FILE" "1" || {
        log_safe "failed (reboot-incident-latch-write-failed)"
        exit 1
    }

    write_atomic_line "$HEALTHY_STREAK_FILE" "0" || {
        log_safe "failed (healthy-streak-reset-failed)"
        exit 1
    }

    write_atomic_line "$REBOOT_FILE" "$current_epoch" || {
        log_safe "failed (reboot-history-write-failed)"
        exit 1
    }

    write_atomic_line "$FAILURE_FILE" "0" || exit 1

    record_outcome "rebooting" "network-recovery-exhausted" "0" "$stage" "$gateway" || true
    log_safe "rebooting (network-recovery-exhausted)"

    if ! "$SYSTEMCTL_BIN" reboot; then
        record_outcome "failed" "reboot-request-failed" "0" "$stage" "$gateway" || true
        log_safe "failed (reboot-request-failed)"
        exit 1
    fi
}

case "${1:-run}" in
    run)
        run_watchdog
        ;;
    *)
        printf 'Usage: %s run\n' "$0" >&2
        exit 2
        ;;
esac
