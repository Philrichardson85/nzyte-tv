#!/usr/bin/env bash

set -u
set -o pipefail

readonly APP_BIN="${NZYTE_TV_PRODUCTION_APP:-/opt/nzyte-tv/app/nzytetv}"
readonly CONFIG_PATH="${NZYTE_TV_PRODUCTION_ROLLING_CONFIG:-/etc/nzyte-tv/rolling-station.json}"
readonly ROLLING_STATE_PATH="${NZYTE_TV_PRODUCTION_ROLLING_STATE:-/var/lib/nzyte-tv/rolling-state.json}"
readonly CUTOVER_MARKER="${NZYTE_TV_PRODUCTION_CUTOVER_MARKER:-/var/lib/nzyte-tv/accept-stopped-static-cutover}"

if [[ ! -x "$APP_BIN" ]]; then
    printf 'NZYTE TV production launch failed: application unavailable.\n' >&2
    exit 78
fi

if [[ ! -r "$CONFIG_PATH" ]]; then
    printf 'NZYTE TV production launch failed: rolling configuration unavailable.\n' >&2
    exit 78
fi

if [[ -e "$ROLLING_STATE_PATH" ]]; then
    exec "$APP_BIN" station rolling run --config "$CONFIG_PATH"
fi

if [[ -f "$CUTOVER_MARKER" && -r "$CUTOVER_MARKER" ]]; then
    exec "$APP_BIN" station rolling run \
        --config "$CONFIG_PATH" \
        --accept-stopped-static-cutover
fi

exec "$APP_BIN" station rolling run --config "$CONFIG_PATH"
