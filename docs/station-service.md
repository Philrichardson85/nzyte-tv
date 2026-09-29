# Station supervisor and manual systemd operation

NZYTE TV v0.5.0 Checkpoint 1 adds a station-level supervisor above the existing resilient broadcaster. It validates a fixed playlist queue, maintains atomic runtime state and a heartbeat, reports status without exposing process arguments, and can run as a manually controlled systemd service.

This is foundation work, not persistent reboot resume or continuous 24/7 queue generation. A full station-process restart can begin again from the configured static queue. Checkpoint 2 will address persistent playback position across restarts.

## What the supervisor owns

```text
systemd
    -> nzytetv station run
        -> station configuration, runtime state, heartbeat
            -> existing BroadcastRecoveryRunner
                -> FFmpeg
```

The supervisor does not contain a second broadcaster. It uses the existing `BroadcastPlanner`, `BroadcastRecoveryRunner`, `FfmpegBroadcaster`, retry policy, progress parser, stream-copy behavior, cancellation, temporary concat cleanup, and destination redaction.

The configured playlist list is fixed for the lifetime of one `station run` process. The supervisor does not discover new playlist JSON, generate future blocks, persist resume position across a full restart, call the YouTube API, or send alerts.

## Station configuration

Start from [the repository example](../deploy/config/station.json.example) and install it as `/etc/nzyte-tv/station.json`:

```json
{
  "schemaVersion": 1,
  "mediaRoot": "/srv/nzyte-tv/media",
  "libraryRoot": "/srv/nzyte-tv/media/library",
  "statePath": "/var/lib/nzyte-tv/state.json",
  "playlists": [
    "/srv/nzyte-tv/media/playlists/production-01.json",
    "/srv/nzyte-tv/media/playlists/production-02.json"
  ]
}
```

All paths are absolute. Configuration is non-secret: never add an RTMP/RTMPS URL, stream key, token, or password. Schema version 1 requires an available media root, an available library root, a valid state-file path, and at least one existing, unique playlist file.

## Destination secret

The live destination remains in `NZYTE_TV_RTMP_URL`; it is never stored in station configuration or runtime state. For systemd, create a root-controlled environment file:

```bash
sudo editor /etc/nzyte-tv/secrets.env
sudo chown root:root /etc/nzyte-tv/secrets.env
sudo chmod 600 /etc/nzyte-tv/secrets.env
```

The file has this form; replace the placeholder locally and never commit or document the real value:

```text
NZYTE_TV_RTMP_URL=<SECRET>
```

NZYTE TV redacts the destination from application diagnostics. FFmpeg still receives it as an output argument, so never inspect or publish full FFmpeg process command lines. Use `pgrep -x ffmpeg` for PID-only checks and `pgrep -x -c ffmpeg` for a count.

## Validate before running

Validation does not start FFmpeg or mutate the configuration:

```bash
/opt/nzyte-tv/app/nzytetv station validate \
  --config /etc/nzyte-tv/station.json
```

It validates configuration, roots, playlist JSON, playlist media, technical manifests, and FFmpeg availability by reusing broadcast planning/readiness checks. Without printing the destination value, the destination line reports `NOT CONFIGURED`, `CONFIGURED / VALID`, or `CONFIGURED / INVALID` using the same RTMP/RTMPS validation as a live run. An absent destination does not prevent configuration/media validation because systemd can supply it later; a configured but malformed destination makes the overall result `NOT READY`, and `station run` requires a valid destination.

## Run and inspect without systemd

For a manual shell test, securely export `NZYTE_TV_RTMP_URL` as described in [Broadcasting generated playlists](broadcasting.md), then run:

```bash
/opt/nzyte-tv/app/nzytetv station run \
  --config /etc/nzyte-tv/station.json
```

In another shell:

```bash
/opt/nzyte-tv/app/nzytetv station status
```

The default state path is `/var/lib/nzyte-tv/state.json`. A test or alternate deployment can use:

```bash
/opt/nzyte-tv/app/nzytetv station status --state /some/path/state.json
```

Runtime-state schema version 1 records:

- `stationState`, `stationPid`, `startedAtUtc`, and `lastHeartbeatUtc`;
- `currentPlaylist`, its one-based `currentPlaylistIndex`, `currentSequence`, playlist item count, `assetId`, `title`, and `type` when known;
- `ffmpegPid`, `broadcastState`, and consecutive `recoveryAttempts`;
- `queuedPlaylistCount` and `totalPlaylistCount`; and
- `lastError`, `stoppedAtUtc`, or `completedAtUtc` when applicable.

It never contains the RTMPS destination, environment secrets, scheduling history, or OAuth tokens. Writes use a flushed temporary file followed by an atomic move, so the live JSON is not overwritten in place.

The active supervisor writes `lastHeartbeatUtc` about every 10 seconds. `station status` reports an otherwise active state as `STALE` when the heartbeat is older than 30 seconds or the recorded station PID no longer exists. It checks PIDs without displaying process command lines. Status intentionally reports `YouTube monitoring: NOT CONFIGURED`.

## Install for manual systemd control

The service currently runs as the established production user `u24`. On the Pi, from the repository checkout:

```bash
sudo mkdir -p /etc/nzyte-tv
sudo cp deploy/config/station.json.example /etc/nzyte-tv/station.json
sudo editor /etc/nzyte-tv/station.json

sudo cp deploy/config/secrets.env.example /etc/nzyte-tv/secrets.env
sudo editor /etc/nzyte-tv/secrets.env
sudo chown root:root /etc/nzyte-tv/secrets.env
sudo chmod 600 /etc/nzyte-tv/secrets.env

sudo cp deploy/systemd/nzyte-tv.service /etc/systemd/system/nzyte-tv.service
sudo systemctl daemon-reload
```

The unit's `StateDirectory=nzyte-tv` creates `/var/lib/nzyte-tv` for user `u24` when the service starts; `StateDirectoryMode=0750` and `UMask=0027` keep the generated state file appropriately restricted. The state contains no destination secret.

Validate the installed configuration before starting. If running validation from an ordinary shell, `Destination env: NOT CONFIGURED` is expected when only systemd's environment file contains the secret.

Start and inspect manually:

```bash
sudo systemctl start nzyte-tv
sudo systemctl status nzyte-tv
/opt/nzyte-tv/app/nzytetv station status
journalctl -u nzyte-tv -n 100
journalctl -u nzyte-tv -f
```

The station writes operational output to stdout/stderr, which systemd captures in journald. There is no separate application log-file subsystem, and the existing broadcaster destination redaction remains active.

Stop cleanly:

```bash
sudo systemctl stop nzyte-tv
pgrep -x ffmpeg
```

The final process check should print nothing. SIGTERM follows the supervisor cancellation path: heartbeat and recovery stop, FFmpeg's process tree is terminated, retry is not started, and final station state becomes `stopped`.

> **Do not run `systemctl enable nzyte-tv` yet.** Boot-time enablement is intentionally deferred until Checkpoint 2 persistent resume is accepted. In Checkpoint 1, a station-process restart can replay the configured static queue from its normal starting behavior.

## Why the service uses `Restart=on-failure`

The repository unit uses `Restart=on-failure`, not `Restart=always`. When every configured playlist completes, station state becomes `completed`, `station run` exits zero, and systemd leaves the service completed. It must not automatically replay the queue from item 1. An unrecoverable station failure exits nonzero and is eligible for the unit's five-second restart policy.

Automatic future-playlist generation, continuous queue advancement, persistent reboot resume, YouTube health monitoring, alerts, and automatic service enablement are later checkpoints.
