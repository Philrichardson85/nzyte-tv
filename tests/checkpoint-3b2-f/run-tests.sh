#!/usr/bin/env bash

set -euo pipefail

PATH=/usr/bin:/bin:${PATH}
export PATH

readonly TEST_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
readonly REPOSITORY_ROOT="$(cd -- "${TEST_DIR}/../.." && pwd -P)"
readonly HANDOFF_SCRIPT="${REPOSITORY_ROOT}/deploy/checkpoint-3b2-f/nzyte-tv-production-handoff.sh"
readonly LAUNCH_SCRIPT="${REPOSITORY_ROOT}/deploy/checkpoint-3b2-f/nzyte-tv-production-launch.sh"
readonly MOCK_TOOL="${TEST_DIR}/mock-tool.sh"
readonly TEMPORARY_ROOT="$(mktemp -d)"

CASE_ROOT=""
CONTROL_DIR=""

cleanup() {
    rm -rf -- "$TEMPORARY_ROOT"
}
trap cleanup EXIT

fail() {
    printf 'FAILED: %s\n' "$1" >&2
    exit 1
}

assert_equal() {
    [[ "$1" == "$2" ]] || fail "$3: expected '$2', got '$1'"
}

assert_event() {
    grep -Fx -- "$1" "${CONTROL_DIR}/events" >/dev/null || fail "$2"
}

event_count() {
    grep -c -- "$1" "${CONTROL_DIR}/events" 2>/dev/null || true
}

write_control() {
    printf '%s\n' "$2" > "${CONTROL_DIR}/$1"
}

new_case() {
    local name="$1"
    CASE_ROOT="${TEMPORARY_ROOT}/${name}"
    CONTROL_DIR="${CASE_ROOT}/control"
    mkdir -p "${CASE_ROOT}/bin" "$CONTROL_DIR" "${CASE_ROOT}/proc/2001"
    for tool in systemctl pgrep flock sleep; do
        cp -- "$MOCK_TOOL" "${CASE_ROOT}/bin/${tool}"
        chmod 755 "${CASE_ROOT}/bin/${tool}"
    done
    : > "${CONTROL_DIR}/events"
    write_control production-active 0
    write_control production-enabled disabled
    write_control production-main-pid 3001
    write_control production-ffmpeg-pid 4001
    write_control production-timer-active 0
    write_control production-timer-enabled disabled
    write_control production-recovery-active 0
    write_control test-active 1
    write_control test-enabled enabled
    write_control test-main-pid 1001
    write_control test-ffmpeg-pid 2001
    write_control test-timer-active 1
    write_control test-timer-enabled enabled
    write_control test-recovery-active 0
    write_control app-count 1
    write_control app-pid 1001
    write_control ffmpeg-count 1
    write_control ffmpeg-pid 2001
    write_control production-start-fail 0
    write_control test-start-fail 0
    write_control enable-delay 0
    printf 'Name:\tffmpeg\nPPid:\t1001\n' > "${CASE_ROOT}/proc/2001/status"

    export F_SYSTEMCTL_BIN="${CASE_ROOT}/bin/systemctl"
    export F_PGREP_BIN="${CASE_ROOT}/bin/pgrep"
    export F_FLOCK_BIN="${CASE_ROOT}/bin/flock"
    export F_SLEEP_BIN="${CASE_ROOT}/bin/sleep"
    export F_PROC_ROOT="${CASE_ROOT}/proc"
    export F_LOCK_FILE="${CASE_ROOT}/handoff.lock"
    export F_STOP_TIMEOUT_SECONDS=2
    export F_START_TIMEOUT_SECONDS=2
    export F_POLL_SECONDS=1
    export F_TEST_CONTROL_DIR="$CONTROL_DIR"
}

run_handoff() {
    rm -rf -- "${CONTROL_DIR}/flock-held"
    bash "$HANDOFF_SCRIPT" "$1" >/dev/null
}

set_production_running() {
    write_control production-active 1
    write_control production-enabled enabled
    write_control test-active 0
    write_control test-enabled disabled
    write_control production-timer-enabled enabled
    write_control test-timer-enabled disabled
    write_control app-count 1
    write_control app-pid 3001
    write_control ffmpeg-count 1
    write_control ffmpeg-pid 4001
    mkdir -p "${CASE_ROOT}/proc/4001"
    printf 'Name:\tffmpeg\nPPid:\t3001\n' > "${CASE_ROOT}/proc/4001/status"
}

test_cutover_orders_exclusive_handoff() {
    new_case cutover
    run_handoff cutover
    assert_event "disable nzyte-tv-3b2e-boot-reconnect.timer" "test recovery was not disabled"
    assert_event "disable nzyte-tv-3b2d.service" "test service was not disabled"
    assert_event "enable nzyte-tv.service" "production service was not enabled"
    assert_equal "$(<"${CONTROL_DIR}/production-active")" 1 "production active"
    assert_equal "$(<"${CONTROL_DIR}/test-active")" 0 "test inactive"
    assert_equal "$(<"${CONTROL_DIR}/app-count")" 1 "application count"
    assert_equal "$(<"${CONTROL_DIR}/ffmpeg-count")" 1 "FFmpeg count"
    assert_equal "$(<"${CONTROL_DIR}/production-timer-enabled")" disabled "production recovery remains disabled"
}

test_cutover_failure_leaves_no_broadcaster() {
    new_case cutover-failure
    write_control production-start-fail 1
    if run_handoff cutover; then
        fail "failed production start unexpectedly succeeded"
    fi
    assert_equal "$(<"${CONTROL_DIR}/production-active")" 0 "failed production inactive"
    assert_equal "$(<"${CONTROL_DIR}/test-active")" 0 "test remains inactive after failure"
    assert_equal "$(<"${CONTROL_DIR}/app-count")" 0 "failed cutover application count"
    assert_equal "$(<"${CONTROL_DIR}/ffmpeg-count")" 0 "failed cutover FFmpeg count"
}

test_invalid_ownership_prevents_cutover() {
    new_case invalid-ownership
    printf 'Name:\tffmpeg\nPPid:\t9999\n' > "${CASE_ROOT}/proc/2001/status"
    if run_handoff cutover; then
        fail "unowned FFmpeg unexpectedly allowed cutover"
    fi
    assert_equal "$(event_count '^(enable|disable) ')" 0 "ownership failure service actions"
}

test_running_recovery_action_prevents_cutover() {
    new_case recovery-in-progress
    write_control test-recovery-active 1
    if run_handoff cutover; then
        fail "cutover continued while test recovery was active"
    fi
    assert_equal "$(event_count '^disable nzyte-tv-3b2d.service$')" 0 "service stops during recovery"
    assert_equal "$(event_count '^enable nzyte-tv.service$')" 0 "production starts during recovery"
}

test_concurrent_cutovers_cannot_both_act() {
    new_case concurrent
    write_control enable-delay 0.4
    rm -rf -- "${CONTROL_DIR}/flock-held"
    bash "$HANDOFF_SCRIPT" cutover >/dev/null &
    local first=$!
    bash "$HANDOFF_SCRIPT" cutover >/dev/null 2>&1 || true &
    local second=$!
    wait "$first"
    wait "$second"
    assert_equal "$(event_count '^enable nzyte-tv.service$')" 1 "concurrent production enables"
}

test_rollback_is_two_safe_phases() {
    new_case rollback
    set_production_running
    run_handoff rollback-stop-production
    assert_equal "$(<"${CONTROL_DIR}/production-active")" 0 "rollback production inactive"
    assert_equal "$(<"${CONTROL_DIR}/app-count")" 0 "rollback stop application count"
    assert_equal "$(<"${CONTROL_DIR}/ffmpeg-count")" 0 "rollback stop FFmpeg count"

    run_handoff rollback-start-test
    assert_equal "$(<"${CONTROL_DIR}/production-active")" 0 "rollback production remains inactive"
    assert_equal "$(<"${CONTROL_DIR}/test-active")" 1 "rollback test active"
    assert_equal "$(<"${CONTROL_DIR}/test-timer-enabled")" enabled "rollback test recovery enabled"
    assert_equal "$(<"${CONTROL_DIR}/app-count")" 1 "rollback application count"
    assert_equal "$(<"${CONTROL_DIR}/ffmpeg-count")" 1 "rollback FFmpeg count"
}

test_rollback_refuses_test_start_while_production_active() {
    new_case rollback-guard
    set_production_running
    if run_handoff rollback-start-test; then
        fail "rollback started test while production was active"
    fi
    assert_equal "$(event_count '^enable nzyte-tv-3b2d.service$')" 0 "unsafe test enables"
}

test_launch_marker_is_first_run_only() {
    local root="${TEMPORARY_ROOT}/launch"
    mkdir -p "$root"
    local mock_app="${root}/nzytetv"
    local config="${root}/rolling.json"
    local state="${root}/rolling-state.json"
    local marker="${root}/accept-marker"
    local output="${root}/args"
    printf '{}\n' > "$config"
    printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$*" > "%s"\n' "$output" > "$mock_app"
    chmod 755 "$mock_app"

    NZYTE_TV_PRODUCTION_APP="$mock_app" \
    NZYTE_TV_PRODUCTION_ROLLING_CONFIG="$config" \
    NZYTE_TV_PRODUCTION_ROLLING_STATE="$state" \
    NZYTE_TV_PRODUCTION_CUTOVER_MARKER="$marker" \
        bash "$LAUNCH_SCRIPT"
    ! grep -F -- '--accept-stopped-static-cutover' "$output" >/dev/null \
        || fail "launch accepted cutover without marker"

    : > "$marker"
    NZYTE_TV_PRODUCTION_APP="$mock_app" \
    NZYTE_TV_PRODUCTION_ROLLING_CONFIG="$config" \
    NZYTE_TV_PRODUCTION_ROLLING_STATE="$state" \
    NZYTE_TV_PRODUCTION_CUTOVER_MARKER="$marker" \
        bash "$LAUNCH_SCRIPT"
    grep -F -- '--accept-stopped-static-cutover' "$output" >/dev/null \
        || fail "launch omitted explicit first-run acceptance"

    printf '{}\n' > "$state"
    NZYTE_TV_PRODUCTION_APP="$mock_app" \
    NZYTE_TV_PRODUCTION_ROLLING_CONFIG="$config" \
    NZYTE_TV_PRODUCTION_ROLLING_STATE="$state" \
    NZYTE_TV_PRODUCTION_CUTOVER_MARKER="$marker" \
        bash "$LAUNCH_SCRIPT"
    ! grep -F -- '--accept-stopped-static-cutover' "$output" >/dev/null \
        || fail "launch reused cutover acceptance after rolling state existed"
}

test_cutover_orders_exclusive_handoff
test_cutover_failure_leaves_no_broadcaster
test_invalid_ownership_prevents_cutover
test_running_recovery_action_prevents_cutover
test_concurrent_cutovers_cannot_both_act
test_rollback_is_two_safe_phases
test_rollback_refuses_test_start_while_production_active
test_launch_marker_is_first_run_only

printf 'Checkpoint 3B2-F deployment mock tests passed.\n'
