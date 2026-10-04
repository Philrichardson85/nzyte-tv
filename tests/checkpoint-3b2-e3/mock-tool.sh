#!/usr/bin/env bash

set -u

readonly CONTROL_DIR="${E3_TEST_CONTROL_DIR:?}"
readonly TOOL_NAME="${0##*/}"

read_value() {
    local name="$1"
    local fallback="$2"
    if [[ -r "${CONTROL_DIR}/${name}" ]]; then
        IFS= read -r value < "${CONTROL_DIR}/${name}" || value="$fallback"
        printf '%s\n' "$value"
    else
        printf '%s\n' "$fallback"
    fi
}

write_value() {
    printf '%s\n' "$2" > "${CONTROL_DIR}/$1"
}

decrement_failure() {
    local name="$1"
    local remaining
    remaining="$(read_value "$name" 0)"
    if (( remaining > 0 )); then
        write_value "$name" "$((remaining - 1))"
        return 0
    fi
    return 1
}

service_active_value() {
    if [[ "$1" == "nzyte-tv.service" ]]; then
        read_value production-active 0
    else
        read_value test-active 1
    fi
}

mock_systemctl() {
    local command="${1:-}"
    shift || true
    case "$command" in
        is-active)
            local quiet=0
            if [[ "${1:-}" == "--quiet" ]]; then
                quiet=1
                shift
            fi
            local active
            active="$(service_active_value "${1:-}")"
            if [[ "$active" == "1" ]]; then
                (( quiet == 1 )) || printf 'active\n'
                return 0
            fi
            (( quiet == 1 )) || printf 'inactive\n'
            return 3
            ;;
        is-enabled)
            if [[ "${1:-}" == "nzyte-tv.service" ]]; then
                printf '%s\n' "$(read_value production-enablement disabled)"
            else
                printf '%s\n' "$(read_value test-enablement enabled)"
            fi
            return 1
            ;;
        show)
            printf '%s\n' "$(read_value main-pid 1001)"
            ;;
        stop)
            printf 'stop %s\n' "${1:-}" >> "${CONTROL_DIR}/events"
            if decrement_failure stop-failures; then
                return 1
            fi
            write_value test-active 0
            write_value app-count 0
            write_value ffmpeg-count 0
            ;;
        start)
            printf 'start %s\n' "${1:-}" >> "${CONTROL_DIR}/events"
            if decrement_failure start-failures; then
                return 1
            fi
            write_value test-active 1
            write_value main-pid "$(read_value started-main-pid 1101)"
            write_value app-count 1
            write_value app-pid "$(read_value started-main-pid 1101)"
            write_value ffmpeg-count 1
            write_value ffmpeg-pid "$(read_value started-ffmpeg-pid 2101)"
            ;;
        *)
            return 64
            ;;
    esac
}

mock_pgrep() {
    if [[ "${1:-}" != "-x" ]]; then
        return 64
    fi
    shift
    if [[ "${1:-}" == "-c" ]]; then
        shift
        local count
        if [[ "${1:-}" == "nzytetv" ]]; then
            count="$(read_value app-count 1)"
        else
            count="$(read_value ffmpeg-count 1)"
        fi
        printf '%s\n' "$count"
        (( count > 0 ))
        return
    fi

    if [[ "${1:-}" == "nzytetv" ]]; then
        [[ "$(read_value app-count 1)" == "1" ]] || return 1
        read_value app-pid 1001
    else
        [[ "$(read_value ffmpeg-count 1)" == "1" ]] || return 1
        read_value ffmpeg-pid 2001
    fi
}

mock_flock() {
    mkdir "${CONTROL_DIR}/flock-held" 2>/dev/null
}

mock_sleep() {
    local real_delay
    real_delay="$(read_value real-sleep 0)"
    if [[ "$real_delay" != "0" ]]; then
        /usr/bin/sleep "$real_delay"
    fi
    if [[ "$(read_value advance-enabled 1)" == "1" ]]; then
        write_value diagnostic-output "$(( $(read_value diagnostic-output 1000000) + 1000000 ))"
        write_value diagnostic-epoch "$(( $(read_value diagnostic-epoch 1) + 1 ))"
    fi
}

mock_diagnostics_reader() {
    [[ "$(read_value diagnostics-valid 1)" == "1" ]] || return 1
    printf '%s\t%s\t%s\t%s\n' \
        "$(read_value diagnostic-attempt aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa)" \
        "$(read_value diagnostic-pid 2001)" \
        "$(read_value diagnostic-output 1000000)" \
        "$(read_value diagnostic-epoch 1)"
}

case "$TOOL_NAME" in
    systemctl) mock_systemctl "$@" ;;
    pgrep) mock_pgrep "$@" ;;
    flock) mock_flock "$@" ;;
    sleep) mock_sleep "$@" ;;
    diagnostics-reader) mock_diagnostics_reader "$@" ;;
    *) exit 64 ;;
esac
