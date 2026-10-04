#!/usr/bin/env bash

set -euo pipefail

PATH=/usr/bin:/bin:${PATH}
export PATH

readonly TEST_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
readonly REPOSITORY_ROOT="$(cd -- "${TEST_DIR}/../.." && pwd -P)"
readonly E3_SCRIPT="${REPOSITORY_ROOT}/deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.sh"
readonly DIAGNOSTICS_READER="${REPOSITORY_ROOT}/deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-read-diagnostics.py"
readonly MOCK_TOOL="${TEST_DIR}/mock-tool.sh"
readonly PYTHON_BIN="${E3_TEST_PYTHON_BIN:-python3}"
readonly BOOT_ONE="11111111-1111-4111-8111-111111111111"
readonly BOOT_TWO="22222222-2222-4222-8222-222222222222"
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

assert_file_line() {
    grep -Fx -- "$2" "$1" >/dev/null || fail "$3"
}

event_count() {
    local event="$1"
    grep -c "^${event} " "${CONTROL_DIR}/events" 2>/dev/null || true
}

write_control() {
    printf '%s\n' "$2" > "${CONTROL_DIR}/$1"
}

clear_mock_lock() {
    rm -rf -- "${CONTROL_DIR}/flock-held"
}

new_case() {
    local name="$1"
    local now_epoch
    CASE_ROOT="${TEMPORARY_ROOT}/${name}"
    CONTROL_DIR="${CASE_ROOT}/control"
    mkdir -p "${CASE_ROOT}/bin" "$CONTROL_DIR" "${CASE_ROOT}/state" "${CASE_ROOT}/proc/2001"
    for tool in systemctl pgrep flock sleep diagnostics-reader; do
        cp -- "$MOCK_TOOL" "${CASE_ROOT}/bin/${tool}"
        chmod 755 "${CASE_ROOT}/bin/${tool}"
    done

    now_epoch="$(date -u +%s)"
    printf '%s\n' "$BOOT_ONE" > "${CASE_ROOT}/boot-id"
    printf '{}\n' > "${CASE_ROOT}/diagnostics.json"
    : > "${CONTROL_DIR}/events"
    write_control production-active 0
    write_control production-enablement disabled
    write_control test-active 1
    write_control main-pid 1001
    write_control app-count 1
    write_control app-pid 1001
    write_control ffmpeg-count 1
    write_control ffmpeg-pid 2001
    write_control diagnostic-attempt aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
    write_control diagnostic-pid 2001
    write_control diagnostic-output 1000000
    write_control diagnostic-epoch "$now_epoch"
    write_control diagnostics-valid 1
    write_control advance-enabled 1
    write_control stop-failures 0
    write_control start-failures 0
    write_control real-sleep 0
    printf 'Name:\tffmpeg\nPPid:\t1001\n' > "${CASE_ROOT}/proc/2001/status"

    export E3_STATE_DIR="${CASE_ROOT}/state"
    export E3_BOOT_ID_FILE="${CASE_ROOT}/boot-id"
    export E3_PROC_ROOT="${CASE_ROOT}/proc"
    export E3_DIAGNOSTICS_FILE="${CASE_ROOT}/diagnostics.json"
    export E3_SYSTEMCTL_BIN="${CASE_ROOT}/bin/systemctl"
    export E3_PGREP_BIN="${CASE_ROOT}/bin/pgrep"
    export E3_FLOCK_BIN="${CASE_ROOT}/bin/flock"
    export E3_SLEEP_BIN="${CASE_ROOT}/bin/sleep"
    export E3_DIAGNOSTICS_READER_BIN="${CASE_ROOT}/bin/diagnostics-reader"
    export E3_PROGRESS_OBSERVATION_SECONDS=1
    export E3_MAX_PROGRESS_AGE_SECONDS=120
    export E3_STOP_TIMEOUT_SECONDS=2
    export E3_START_TIMEOUT_SECONDS=2
    export E3_POLL_SECONDS=1
    export E3_TEST_CONTROL_DIR="$CONTROL_DIR"
}

seed_case() {
    clear_mock_lock
    bash "$E3_SCRIPT" seed >/dev/null
}

run_case() {
    clear_mock_lock
    bash "$E3_SCRIPT" run >/dev/null
}

move_to_next_boot() {
    printf '%s\n' "$BOOT_TWO" > "${CASE_ROOT}/boot-id"
}

test_diagnostics_reader() {
    local fixture="${TEMPORARY_ROOT}/diagnostics-reader.json"
    local output
    printf '%s\n' '{"schemaVersion":1,"attempts":[{"attemptId":"abcdefabcdefabcdefabcdefabcdefab","ffmpegPid":2345,"processStartTimeUtc":"2026-10-04T12:00:00+00:00","latestOutputTime":"00:02:03.4567890","lastAdvancingProgressTimeUtc":"2026-10-04T12:02:03Z","processExitTimeUtc":null,"processExitCode":null}]}' > "$fixture"
    output="$($PYTHON_BIN "$DIAGNOSTICS_READER" "$fixture")"
    [[ "$output" == $'abcdefabcdefabcdefabcdefabcdefab\t2345\t123456789\t'* ]] \
        || fail "diagnostics reader did not return its safe allowlisted snapshot"

    printf '%s\n' '{"schemaVersion":1,"attempts":[{"attemptId":"abcdefabcdefabcdefabcdefabcdefab","ffmpegPid":2345,"processStartTimeUtc":"2026-10-04T12:00:00+00:00","latestOutputTime":"00:02:03","lastAdvancingProgressTimeUtc":"2026-10-04T12:02:03Z","processExitTimeUtc":"2026-10-04T12:03:00Z","processExitCode":0}]}' > "$fixture"
    if "$PYTHON_BIN" "$DIAGNOSTICS_READER" "$fixture" >/dev/null 2>&1; then
        fail "diagnostics reader accepted an exited attempt"
    fi
}

test_install_seed_does_not_restart() {
    new_case install-seed
    seed_case
    run_case
    assert_equal "$(event_count stop)" 0 "installation seed stop count"
    assert_equal "$(event_count start)" 0 "installation seed start count"
    assert_equal "$(<"${CASE_ROOT}/state/last-attempt-boot-id")" "$BOOT_ONE" "seed marker"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "result=seeded" "seed outcome missing"
}

test_subsequent_boot_reconnects_once() {
    new_case next-boot
    seed_case
    move_to_next_boot
    run_case
    assert_equal "$(event_count stop)" 1 "subsequent boot stop count"
    assert_equal "$(event_count start)" 1 "subsequent boot start count"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "result=success" "success outcome missing"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "reason=guarded-reconnection-completed" "success reason missing"

    run_case
    assert_equal "$(event_count stop)" 1 "same-boot repeat stop count"
    assert_equal "$(event_count start)" 1 "same-boot repeat start count"
}

test_manual_restart_does_not_trigger_e3() {
    new_case manual-restart
    seed_case
    "${CASE_ROOT}/bin/systemctl" stop nzyte-tv-3b2d.service
    "${CASE_ROOT}/bin/systemctl" start nzyte-tv-3b2d.service
    run_case
    assert_equal "$(event_count stop)" 1 "manual restart gained an E3 stop"
    assert_equal "$(event_count start)" 1 "manual restart gained an E3 start"
}

test_production_active_skips() {
    new_case production-active
    seed_case
    move_to_next_boot
    write_control production-active 1
    run_case
    assert_equal "$(event_count stop)" 0 "production-active stop count"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "reason=production-service-active" "production-active skip missing"
}

test_missing_ffmpeg_and_stalled_progress_skip() {
    new_case missing-ffmpeg
    seed_case
    move_to_next_boot
    write_control ffmpeg-count 0
    run_case
    assert_equal "$(event_count stop)" 0 "missing-FFmpeg stop count"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "reason=ffmpeg-process-count-not-one" "missing-FFmpeg skip missing"

    new_case stalled-progress
    seed_case
    move_to_next_boot
    write_control advance-enabled 0
    run_case
    assert_equal "$(event_count stop)" 0 "stalled-progress stop count"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "reason=progress-not-advancing" "stalled-progress skip missing"
}

test_unrelated_ffmpeg_skips() {
    new_case unrelated-ffmpeg
    seed_case
    move_to_next_boot
    printf 'Name:\tffmpeg\nPPid:\t9999\n' > "${CASE_ROOT}/proc/2001/status"
    run_case
    assert_equal "$(event_count stop)" 0 "unrelated-FFmpeg stop count"
    assert_file_line \
        "${CASE_ROOT}/state/last-outcome.env" \
        "reason=ffmpeg-service-process-mismatch" \
        "unrelated-FFmpeg skip missing"
}

test_concurrent_invocations_restart_once() {
    new_case concurrent
    seed_case
    move_to_next_boot
    write_control real-sleep 0.4
    clear_mock_lock
    bash "$E3_SCRIPT" run >/dev/null &
    local first_pid=$!
    bash "$E3_SCRIPT" run >/dev/null &
    local second_pid=$!
    wait "$first_pid"
    wait "$second_pid"
    assert_equal "$(event_count stop)" 1 "concurrent stop count"
    assert_equal "$(event_count start)" 1 "concurrent start count"
}

test_failed_stop_and_start_are_bounded() {
    new_case stop-failure
    seed_case
    move_to_next_boot
    write_control stop-failures 1
    if run_case; then
        fail "failed stop unexpectedly succeeded"
    fi
    assert_equal "$(event_count stop)" 1 "failed-stop command count"
    assert_equal "$(event_count start)" 0 "failed-stop restoration count"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "reason=stop-command-failed-service-still-active" "failed-stop outcome missing"

    new_case start-failure
    seed_case
    move_to_next_boot
    write_control start-failures 2
    if run_case; then
        fail "failed start unexpectedly succeeded"
    fi
    assert_equal "$(event_count stop)" 1 "failed-start stop count"
    assert_equal "$(event_count start)" 2 "failed-start bounded start count"
    assert_file_line "${CASE_ROOT}/state/last-outcome.env" "reason=start-command-failed-restore-failed" "failed-start outcome missing"
}

test_diagnostics_reader
test_install_seed_does_not_restart
test_subsequent_boot_reconnects_once
test_manual_restart_does_not_trigger_e3
test_production_active_skips
test_missing_ffmpeg_and_stalled_progress_skip
test_unrelated_ffmpeg_skips
test_concurrent_invocations_restart_once
test_failed_stop_and_start_are_bounded

printf 'Checkpoint 3B2-E3 mock tests passed.\n'
