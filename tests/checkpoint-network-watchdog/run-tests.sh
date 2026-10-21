#!/usr/bin/env bash

set -u
set -o pipefail

REPOSITORY_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
SOURCE_SCRIPT="${REPOSITORY_ROOT}/deploy/checkpoint-network-watchdog/nzyte-tv-network-watchdog.sh"

WORK_ROOT="$(mktemp -d)"
trap 'rm -rf -- "$WORK_ROOT"' EXIT

MOCK_BIN="${WORK_ROOT}/bin"
UNDER_TEST="${WORK_ROOT}/nzyte-tv-network-watchdog-under-test.sh"

mkdir -p -- "$MOCK_BIN"

fail() {
    printf 'FAIL: %s\n' "$1" >&2
    exit 1
}

assert_eq() {
    local expected="$1"
    local actual="$2"
    local message="$3"

    if [[ "$expected" != "$actual" ]]; then
        fail "${message}: expected=${expected} actual=${actual}"
    fi
}

assert_file_contains() {
    local path="$1"
    local expected="$2"
    local message="$3"

    grep -Fqx -- "$expected" "$path" 2>/dev/null \
        || fail "${message}: missing '${expected}'"
}

assert_file_not_contains_text() {
    local path="$1"
    local forbidden="$2"
    local message="$3"

    if grep -Fq -- "$forbidden" "$path" 2>/dev/null; then
        fail "${message}: found '${forbidden}'"
    fi
}

read_state() {
    local path="$1"
    local default_value="$2"

    if [[ -r "$path" ]]; then
        cat -- "$path"
    else
        printf '%s\n' "$default_value"
    fi
}

write_mock() {
    local name="$1"
    local body="$2"
    local path="${MOCK_BIN}/${name}"

    printf '%s\n' '#!/usr/bin/env bash' > "$path"
    printf '%s\n' "$body" >> "$path"
    chmod 755 "$path"
}

# The production script requires EUID 0. Git Bash on Windows is not root, so
# the local harness changes only its temporary copy. The version-controlled
# production source remains byte-for-byte untouched.
sed \
    '/^[[:space:]]*if (( EUID != 0 )); then$/,/^[[:space:]]*fi$/c\
    # Local mock harness: production root gate bypassed in temporary copy only.' \
    "$SOURCE_SCRIPT" > "$UNDER_TEST" \
    || fail "unable to create temporary script under test"

chmod 755 "$UNDER_TEST"

grep -Fq 'if (( EUID != 0 )); then' "$SOURCE_SCRIPT" \
    || fail "production root guard missing"

if grep -Fq 'if (( EUID != 0 )); then' "$UNDER_TEST"; then
    fail "temporary root guard bypass failed"
fi

write_mock "ip" '
if [[ "${MOCK_ROUTE_PRESENT:-1}" == "1" ]]; then
    printf "default via %s proto dhcp src 192.168.86.227 metric 600\n" \
        "${MOCK_GATEWAY:-192.168.86.1}"
    exit 0
fi
exit 0
'

write_mock "ping" '
count=0
if [[ -r "$MOCK_PING_COUNT_FILE" ]]; then
    count="$(cat "$MOCK_PING_COUNT_FILE")"
fi
count=$((count + 1))
printf "%s\n" "$count" > "$MOCK_PING_COUNT_FILE"

result="$(sed -n "${count}p" "$MOCK_PING_SEQUENCE_FILE")"
if [[ -z "$result" ]]; then
    result=1
fi
exit "$result"
'

write_mock "getent" '
exit "${MOCK_DNS_RESULT:-0}"
'

write_mock "nmcli" '
printf "%s\n" "$*" >> "$MOCK_NMCLI_LOG"

case "$*" in
    *"connection down "*)
        exit "${MOCK_NM_DOWN_RESULT:-0}"
        ;;
    *"connection up "*)
        exit "${MOCK_NM_UP_RESULT:-0}"
        ;;
    *"radio wifi off"*)
        exit "${MOCK_RADIO_OFF_RESULT:-0}"
        ;;
    *"radio wifi on"*)
        exit "${MOCK_RADIO_ON_RESULT:-0}"
        ;;
esac

exit 0
'

write_mock "systemctl" '
printf "%s\n" "$*" >> "$MOCK_SYSTEMCTL_LOG"
exit "${MOCK_SYSTEMCTL_RESULT:-0}"
'

write_mock "flock" '
exit 0
'

write_mock "sleep" '
exit 0
'

write_mock "date" '
case "${1:-}" in
    -u)
        case "${2:-}" in
            +%s)
                printf "%s\n" "${MOCK_NOW_EPOCH:-2000000000}"
                ;;
            +%Y-%m-%dT%H:%M:%SZ)
                printf "%s\n" "2033-05-18T03:33:20Z"
                ;;
            *)
                exit 1
                ;;
        esac
        ;;
    *)
        exit 1
        ;;
esac
'

write_mock "timeout" '
printf "%s\n" "$*" >> "$MOCK_TIMEOUT_LOG"
shift
exec "$@"
'

new_case() {
    CASE_ROOT="${WORK_ROOT}/case-$1"
    STATE_DIR="${CASE_ROOT}/state"

    mkdir -p -- "$STATE_DIR"

    MOCK_PING_SEQUENCE_FILE="${CASE_ROOT}/ping-sequence"
    MOCK_PING_COUNT_FILE="${CASE_ROOT}/ping-count"
    MOCK_NMCLI_LOG="${CASE_ROOT}/nmcli.log"
    MOCK_SYSTEMCTL_LOG="${CASE_ROOT}/systemctl.log"
    MOCK_TIMEOUT_LOG="${CASE_ROOT}/timeout.log"
    MOCK_UPTIME_FILE="${CASE_ROOT}/uptime"

    : > "$MOCK_PING_SEQUENCE_FILE"
    : > "$MOCK_NMCLI_LOG"
    : > "$MOCK_SYSTEMCTL_LOG"
    : > "$MOCK_TIMEOUT_LOG"
    printf '0\n' > "$MOCK_PING_COUNT_FILE"
    printf '3600.00 0.00\n' > "$MOCK_UPTIME_FILE"

    export CASE_ROOT
    export STATE_DIR
    export MOCK_PING_SEQUENCE_FILE
    export MOCK_PING_COUNT_FILE
    export MOCK_NMCLI_LOG
    export MOCK_SYSTEMCTL_LOG
    export MOCK_TIMEOUT_LOG
    export MOCK_UPTIME_FILE

    export MOCK_ROUTE_PRESENT=1
    export MOCK_GATEWAY=192.168.86.1
    export MOCK_DNS_RESULT=0
    export MOCK_NM_DOWN_RESULT=0
    export MOCK_NM_UP_RESULT=0
    export MOCK_RADIO_OFF_RESULT=0
    export MOCK_RADIO_ON_RESULT=0
    export MOCK_SYSTEMCTL_RESULT=0
    export MOCK_NOW_EPOCH=2000000000
}

run_case() {
    env \
        NW_STATE_DIR="$STATE_DIR" \
        NW_CONNECTION_NAME="g-route" \
        NW_INTERFACE="wlan0" \
        NW_DNS_NAME="a.rtmp.youtube.com" \
        NW_FAILURE_THRESHOLD=3 \
        NW_VERIFY_ATTEMPTS=3 \
        NW_VERIFY_DELAY_SECONDS=0 \
        NW_PING_TIMEOUT_SECONDS=1 \
        NW_DNS_TIMEOUT_SECONDS=8 \
        NW_MIN_UPTIME_SECONDS=900 \
        NW_REBOOT_COOLDOWN_SECONDS=21600 \
        NW_HEALTHY_REARM_THRESHOLD=5 \
        NW_IP_BIN="${MOCK_BIN}/ip" \
        NW_PING_BIN="${MOCK_BIN}/ping" \
        NW_GETENT_BIN="${MOCK_BIN}/getent" \
        NW_NMCLI_BIN="${MOCK_BIN}/nmcli" \
        NW_SYSTEMCTL_BIN="${MOCK_BIN}/systemctl" \
        NW_FLOCK_BIN="${MOCK_BIN}/flock" \
        NW_SLEEP_BIN="${MOCK_BIN}/sleep" \
        NW_DATE_BIN="${MOCK_BIN}/date" \
        NW_TIMEOUT_BIN="${MOCK_BIN}/timeout" \
        NW_UPTIME_FILE="$MOCK_UPTIME_FILE" \
        MOCK_ROUTE_PRESENT="$MOCK_ROUTE_PRESENT" \
        MOCK_GATEWAY="$MOCK_GATEWAY" \
        MOCK_DNS_RESULT="$MOCK_DNS_RESULT" \
        MOCK_NM_DOWN_RESULT="$MOCK_NM_DOWN_RESULT" \
        MOCK_NM_UP_RESULT="$MOCK_NM_UP_RESULT" \
        MOCK_RADIO_OFF_RESULT="$MOCK_RADIO_OFF_RESULT" \
        MOCK_RADIO_ON_RESULT="$MOCK_RADIO_ON_RESULT" \
        MOCK_SYSTEMCTL_RESULT="$MOCK_SYSTEMCTL_RESULT" \
        MOCK_NOW_EPOCH="$MOCK_NOW_EPOCH" \
        MOCK_PING_SEQUENCE_FILE="$MOCK_PING_SEQUENCE_FILE" \
        MOCK_PING_COUNT_FILE="$MOCK_PING_COUNT_FILE" \
        MOCK_NMCLI_LOG="$MOCK_NMCLI_LOG" \
        MOCK_SYSTEMCTL_LOG="$MOCK_SYSTEMCTL_LOG" \
        MOCK_TIMEOUT_LOG="$MOCK_TIMEOUT_LOG" \
        bash "$UNDER_TEST" run >/dev/null
}

printf '%s\n' '=== Network watchdog mock suite ==='

# 1. Healthy gateway + DNS resets all transient state.
new_case "healthy"
printf '9\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '0\n' > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "healthy case exited nonzero"

assert_eq "0" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "healthy failure reset"
assert_eq "0" "$(read_state "${STATE_DIR}/recovery-stage" missing)" \
    "healthy stage reset"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "result=healthy" \
    "healthy result"
assert_file_not_contains_text "$MOCK_NMCLI_LOG" \
    "connection" \
    "healthy case must not recover"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "healthy case must not reboot"

printf '%s\n' 'PASS healthy'

# 2. DNS failure with reachable gateway is degraded only and does not recover.
new_case "dns-degraded"
MOCK_DNS_RESULT=1
printf '0\n' > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "DNS-degraded case exited nonzero"

assert_eq "0" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "DNS failure must not count as gateway failure"
assert_eq "0" "$(read_state "${STATE_DIR}/recovery-stage" missing)" \
    "DNS failure must not advance recovery"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "result=degraded" \
    "DNS degraded result"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=dns-unresolved-gateway-healthy" \
    "DNS degraded reason"
assert_file_not_contains_text "$MOCK_NMCLI_LOG" \
    "connection" \
    "DNS failure must not touch Wi-Fi"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "DNS failure must not reboot"
grep -Fq \
    "8 ${MOCK_BIN}/getent ahostsv4 a.rtmp.youtube.com" \
    "$MOCK_TIMEOUT_LOG" \
    || fail "DNS probe was not bounded by the configured timeout"

printf '%s\n' 'PASS dns-degraded'

# 3. A single gateway failure increments state but performs no recovery.
new_case "gateway-first-failure"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "first gateway failure case exited nonzero"

assert_eq "1" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "first gateway failure count"
assert_eq "0" "$(read_state "${STATE_DIR}/recovery-stage" 0)" \
    "first gateway failure stage"
assert_file_not_contains_text "$MOCK_NMCLI_LOG" \
    "connection" \
    "first failure must not recover"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "first failure must not reboot"

printf '%s\n' 'PASS gateway-first-failure'

# 4. Third gateway failure triggers connection cycle; successful verification
# resets the state.
new_case "connection-recovery-success"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '0\n' > "${STATE_DIR}/recovery-stage"
printf '%s\n' 1 0 > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "connection recovery success case exited nonzero"

assert_eq "0" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "connection recovery failure reset"
assert_eq "0" "$(read_state "${STATE_DIR}/recovery-stage" missing)" \
    "connection recovery stage reset"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=connection-cycle-succeeded" \
    "connection recovery outcome"
grep -Fq 'connection down g-route' "$MOCK_NMCLI_LOG" \
    || fail "connection recovery did not issue connection down"
grep -Fq 'connection up g-route ifname wlan0' "$MOCK_NMCLI_LOG" \
    || fail "connection recovery did not issue connection up"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "successful connection recovery must not reboot"

printf '%s\n' 'PASS connection-recovery-success'

# 5. Failed connection recovery advances to stage 1 and resets the observation
# counter for a fresh sustained-failure window.
new_case "connection-recovery-failure"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '0\n' > "${STATE_DIR}/recovery-stage"
printf '%s\n' 1 1 1 1 > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "connection recovery failure case exited nonzero"

assert_eq "0" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "failed connection recovery counter reset"
assert_eq "1" "$(read_state "${STATE_DIR}/recovery-stage" missing)" \
    "failed connection recovery stage advancement"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=connection-cycle-failed" \
    "failed connection recovery outcome"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "stage 0 failure must not reboot"

printf '%s\n' 'PASS connection-recovery-failure'

# 6. Stage 1 third failure triggers a radio cycle; successful verification
# returns the watchdog to stage 0.
new_case "radio-recovery-success"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '1\n' > "${STATE_DIR}/recovery-stage"
printf '%s\n' 1 0 > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "radio recovery success case exited nonzero"

assert_eq "0" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "radio recovery failure reset"
assert_eq "0" "$(read_state "${STATE_DIR}/recovery-stage" missing)" \
    "radio recovery stage reset"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=wifi-radio-cycle-succeeded" \
    "radio recovery outcome"
grep -Fq 'radio wifi off' "$MOCK_NMCLI_LOG" \
    || fail "radio recovery did not disable Wi-Fi"
grep -Fq 'radio wifi on' "$MOCK_NMCLI_LOG" \
    || fail "radio recovery did not enable Wi-Fi"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "successful radio recovery must not reboot"

printf '%s\n' 'PASS radio-recovery-success'

# 7. Failed stage 1 recovery advances to stage 2 without rebooting immediately.
new_case "radio-recovery-failure"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '1\n' > "${STATE_DIR}/recovery-stage"
printf '%s\n' 1 1 1 1 > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "radio recovery failure case exited nonzero"

assert_eq "0" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "failed radio recovery counter reset"
assert_eq "2" "$(read_state "${STATE_DIR}/recovery-stage" missing)" \
    "failed radio recovery stage advancement"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=wifi-radio-cycle-failed" \
    "failed radio recovery outcome"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "stage 1 failure must not reboot immediately"

printf '%s\n' 'PASS radio-recovery-failure'

# 8. Stage 2 still requires a fresh three-failure window before reboot.
new_case "stage-two-below-threshold"
printf '1\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "stage two below threshold case exited nonzero"

assert_eq "2" "$(read_state "${STATE_DIR}/gateway-failures" missing)" \
    "stage two observation count"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "stage two below threshold must not reboot"

printf '%s\n' 'PASS stage-two-below-threshold'

# 9. Exhausted recovery with sufficient uptime and no cooldown requests exactly
# one graceful systemd reboot.
new_case "reboot"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"
printf '3600.00 0.00\n' > "$MOCK_UPTIME_FILE"

run_case || fail "reboot case exited nonzero"

assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "result=rebooting" \
    "reboot outcome"
assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=network-recovery-exhausted" \
    "reboot reason"
assert_eq "2000000000" \
    "$(read_state "${STATE_DIR}/last-watchdog-reboot-epoch" missing)" \
    "reboot history"
assert_eq "1" \
    "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "reboot incident latch"
assert_eq "0" \
    "$(read_state "${STATE_DIR}/healthy-streak" missing)" \
    "reboot healthy streak reset"
assert_eq "1" "$(grep -Fxc 'reboot' "$MOCK_SYSTEMCTL_LOG")" \
    "graceful reboot request count"

printf '%s\n' 'PASS reboot'

# 10. Reboot cooldown blocks a reboot loop across invocations/boots.
new_case "reboot-cooldown"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '1999999900\n' > "${STATE_DIR}/last-watchdog-reboot-epoch"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"
printf '3600.00 0.00\n' > "$MOCK_UPTIME_FILE"

run_case || fail "reboot cooldown case exited nonzero"

assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=reboot-suppressed-cooldown" \
    "reboot cooldown outcome"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "cooldown must suppress reboot"

printf '%s\n' 'PASS reboot-cooldown'

# 11. Minimum uptime also prevents an early post-boot reboot.
new_case "minimum-uptime"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"
printf '600.00 0.00\n' > "$MOCK_UPTIME_FILE"

run_case || fail "minimum uptime case exited nonzero"

assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=reboot-suppressed-minimum-uptime" \
    "minimum uptime suppression outcome"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "minimum uptime must suppress reboot"

printf '%s\n' 'PASS minimum-uptime'

# 12. A consumed incident remains unable to reboot even after the ordinary
# six-hour cooldown has elapsed.
new_case "incident-consumed"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '1\n' > "${STATE_DIR}/reboot-consumed"
printf '1900000000\n' > "${STATE_DIR}/last-watchdog-reboot-epoch"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"
printf '3600.00 0.00\n' > "$MOCK_UPTIME_FILE"

run_case || fail "incident-consumed case exited nonzero"

assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=reboot-suppressed-incident-consumed" \
    "incident latch suppression outcome"
assert_eq "1" \
    "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "incident latch must remain consumed"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "consumed incident must not reboot"

printf '%s\n' 'PASS incident-consumed'

# 13. Five consecutive healthy gateway observations are required to re-arm
# the reboot allowance. Four healthy checks are intentionally insufficient.
new_case "healthy-rearm"
printf '1\n' > "${STATE_DIR}/reboot-consumed"
printf '0\n' > "${STATE_DIR}/healthy-streak"
printf '%s\n' 0 0 0 0 0 > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "healthy rearm check 1 exited nonzero"
assert_eq "1" "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "latch after healthy check 1"
assert_eq "1" "$(read_state "${STATE_DIR}/healthy-streak" missing)" \
    "healthy streak after check 1"

run_case || fail "healthy rearm check 2 exited nonzero"
assert_eq "1" "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "latch after healthy check 2"
assert_eq "2" "$(read_state "${STATE_DIR}/healthy-streak" missing)" \
    "healthy streak after check 2"

run_case || fail "healthy rearm check 3 exited nonzero"
assert_eq "1" "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "latch after healthy check 3"
assert_eq "3" "$(read_state "${STATE_DIR}/healthy-streak" missing)" \
    "healthy streak after check 3"

run_case || fail "healthy rearm check 4 exited nonzero"
assert_eq "1" "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "four healthy checks must not re-arm"
assert_eq "4" "$(read_state "${STATE_DIR}/healthy-streak" missing)" \
    "healthy streak after check 4"

run_case || fail "healthy rearm check 5 exited nonzero"
assert_eq "0" "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "fifth healthy check must re-arm"
assert_eq "0" "$(read_state "${STATE_DIR}/healthy-streak" missing)" \
    "healthy streak must reset after re-arm"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "healthy re-arm must never reboot"

printf '%s\n' 'PASS healthy-rearm'

# 14. Any failed gateway observation breaks a partially accumulated healthy
# re-arm streak.
new_case "healthy-streak-break"
printf '1\n' > "${STATE_DIR}/reboot-consumed"
printf '3\n' > "${STATE_DIR}/healthy-streak"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"

run_case || fail "healthy streak break case exited nonzero"

assert_eq "1" "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "failed gateway check must leave latch consumed"
assert_eq "0" "$(read_state "${STATE_DIR}/healthy-streak" missing)" \
    "failed gateway check must clear healthy streak"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "single failed observation must not reboot"

printf '%s\n' 'PASS healthy-streak-break'

# 15. Even when systemd rejects the reboot request, the incident allowance
# was already consumed. A later invocation cannot retry-loop the reboot.
new_case "reboot-request-failure"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '%s\n' 1 1 > "$MOCK_PING_SEQUENCE_FILE"
printf '3600.00 0.00\n' > "$MOCK_UPTIME_FILE"
MOCK_SYSTEMCTL_RESULT=1

if run_case; then
    fail "reboot request failure unexpectedly exited zero"
fi

assert_eq "1" "$(read_state "${STATE_DIR}/reboot-consumed" missing)" \
    "failed reboot request must consume incident"
assert_eq "2000000000" \
    "$(read_state "${STATE_DIR}/last-watchdog-reboot-epoch" missing)" \
    "failed reboot request history"
assert_eq "1" "$(grep -Fxc 'reboot' "$MOCK_SYSTEMCTL_LOG")" \
    "initial failed reboot request count"

: > "$MOCK_SYSTEMCTL_LOG"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
MOCK_NOW_EPOCH=2000100000
MOCK_SYSTEMCTL_RESULT=0

run_case || fail "post-failed-reboot latch case exited nonzero"

assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=reboot-suppressed-incident-consumed" \
    "failed reboot request follow-up suppression"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "failed reboot request must not be retried for same incident"

printf '%s\n' 'PASS reboot-request-failure-latched'

# 16. Corrupt incident-latch state fails safe rather than allowing reboot.
new_case "invalid-incident-state"
printf '2\n' > "${STATE_DIR}/gateway-failures"
printf '2\n' > "${STATE_DIR}/recovery-stage"
printf '7\n' > "${STATE_DIR}/reboot-consumed"
printf '1\n' > "$MOCK_PING_SEQUENCE_FILE"
printf '3600.00 0.00\n' > "$MOCK_UPTIME_FILE"

run_case || fail "invalid incident state case exited nonzero"

assert_file_contains "${STATE_DIR}/last-outcome.env" \
    "reason=reboot-suppressed-invalid-incident-state" \
    "invalid incident state outcome"
assert_file_not_contains_text "$MOCK_SYSTEMCTL_LOG" \
    "reboot" \
    "invalid incident state must fail safe"

printf '%s\n' 'PASS invalid-incident-state'

printf '%s\n' 'Network watchdog mock tests passed.'
