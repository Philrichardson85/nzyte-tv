#!/usr/bin/env bash

set -u

readonly CONTROL_DIR="${F_TEST_CONTROL_DIR:?}"
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

unit_prefix() {
    case "$1" in
        nzyte-tv.service) printf 'production\n' ;;
        nzyte-tv-3b2d.service) printf 'test\n' ;;
        nzyte-tv-boot-reconnect.timer) printf 'production-timer\n' ;;
        nzyte-tv-3b2e-boot-reconnect.timer) printf 'test-timer\n' ;;
        nzyte-tv-boot-reconnect.service) printf 'production-recovery\n' ;;
        nzyte-tv-3b2e-boot-reconnect.service) printf 'test-recovery\n' ;;
        *) return 1 ;;
    esac
}

set_processes_for_service() {
    local prefix="$1"
    local app_pid
    local ffmpeg_pid
    app_pid="$(read_value "${prefix}-main-pid" 1001)"
    ffmpeg_pid="$(read_value "${prefix}-ffmpeg-pid" 2001)"
    write_value app-count 1
    write_value app-pid "$app_pid"
    write_value ffmpeg-count 1
    write_value ffmpeg-pid "$ffmpeg_pid"
    mkdir -p -- "${F_PROC_ROOT}/${ffmpeg_pid}"
    printf 'Name:\tffmpeg\nPPid:\t%s\n' "$app_pid" > "${F_PROC_ROOT}/${ffmpeg_pid}/status"
}

mock_systemctl() {
    local command="${1:-}"
    shift || true
    case "$command" in
        is-active)
            local prefix
            prefix="$(unit_prefix "${1:-}")" || return 4
            if [[ "$(read_value "${prefix}-active" 0)" == "1" ]]; then
                printf 'active\n'
                return 0
            fi
            printf 'inactive\n'
            return 3
            ;;
        is-enabled)
            local prefix
            prefix="$(unit_prefix "${1:-}")" || return 4
            read_value "${prefix}-enabled" disabled
            return 1
            ;;
        show)
            local prefix
            prefix="$(unit_prefix "${1:-}")" || return 4
            read_value "${prefix}-main-pid" 1001
            ;;
        disable)
            [[ "${1:-}" == "--now" ]] || return 64
            shift
            local prefix
            prefix="$(unit_prefix "${1:-}")" || return 4
            printf 'disable %s\n' "${1:-}" >> "${CONTROL_DIR}/events"
            write_value "${prefix}-enabled" disabled
            write_value "${prefix}-active" 0
            if [[ "$prefix" == "production" || "$prefix" == "test" ]]; then
                write_value app-count 0
                write_value ffmpeg-count 0
            fi
            ;;
        enable)
            [[ "${1:-}" == "--now" ]] || return 64
            shift
            local prefix
            prefix="$(unit_prefix "${1:-}")" || return 4
            printf 'enable %s\n' "${1:-}" >> "${CONTROL_DIR}/events"
            if [[ "$prefix" == "production" \
                && "$(read_value production-start-fail 0)" == "1" ]]; then
                return 1
            fi
            if [[ "$prefix" == "test" \
                && "$(read_value test-start-fail 0)" == "1" ]]; then
                return 1
            fi
            local delay
            delay="$(read_value enable-delay 0)"
            [[ "$delay" == "0" ]] || /usr/bin/sleep "$delay"
            write_value "${prefix}-enabled" enabled
            write_value "${prefix}-active" 1
            if [[ "$prefix" == "production" || "$prefix" == "test" ]]; then
                set_processes_for_service "$prefix"
            fi
            ;;
        *) return 64 ;;
    esac
}

mock_pgrep() {
    [[ "${1:-}" == "-x" ]] || return 64
    shift
    if [[ "${1:-}" == "-c" ]]; then
        shift
        local count
        if [[ "${1:-}" == "nzytetv" ]]; then
            count="$(read_value app-count 0)"
        else
            count="$(read_value ffmpeg-count 0)"
        fi
        printf '%s\n' "$count"
        (( count > 0 ))
        return
    fi

    if [[ "${1:-}" == "nzytetv" ]]; then
        [[ "$(read_value app-count 0)" == "1" ]] || return 1
        read_value app-pid 1001
    else
        [[ "$(read_value ffmpeg-count 0)" == "1" ]] || return 1
        read_value ffmpeg-pid 2001
    fi
}

case "$TOOL_NAME" in
    systemctl) mock_systemctl "$@" ;;
    pgrep) mock_pgrep "$@" ;;
    flock) mkdir "${CONTROL_DIR}/flock-held" 2>/dev/null ;;
    sleep) exit 0 ;;
    *) exit 64 ;;
esac
