# Station supervisor, persistent resume, and systemd operation

NZYTE TV v0.5.0 Checkpoint 2 extends the station foundation with safe item-level resume across a full station-process restart. It has passed Raspberry Pi acceptance for hard parent failure, clean stop/start, graceful reboot, and boot-enabled reboot. It still broadcasts only the fixed ordered playlist list in `station.json`; it does not discover or consume future playlists. Checkpoint 3B1 may prepare immutable rolling blocks separately, but those artifacts do not activate rolling station behavior.

The supervisor continues to wrap, rather than replace, the existing resilient broadcaster:

```text
systemd
    -> nzytetv station run
        -> configuration, schema-v2 runtime state, heartbeat, resume decision
            -> existing BroadcastRecoveryRunner
                -> FFmpeg
```

FFmpeg recovery and persistent resume are separate:

- `recoveryAttempts` counts replacement FFmpeg attempts within one station process.
- `resumeCount` counts cold resumes across station-process starts.
- Both restart the interrupted item from its beginning. Neither seeks to an exact timestamp or frame.

Checkpoint 2 does not add dynamic queue discovery, scheduling-history mutation, continuous queue advancement, YouTube API monitoring, or alerts. The separate Checkpoint 3B1 planner can generate future blocks, but station execution/handoff is reserved for 3B2. YouTube monitoring remains `NOT CONFIGURED`.

## Station configuration and destination secret

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

Configuration schema version 1 remains non-secret and requires absolute paths, available media and library roots, a valid state-file path, and at least one existing unique playlist. The playlist list and order are fixed for one configured queue.

The live destination remains in `NZYTE_TV_RTMP_URL`; it is never part of station configuration, queue identity, runtime state, status, or application diagnostics. For systemd, use a root-controlled environment file:

```bash
sudo editor /etc/nzyte-tv/secrets.env
sudo chown root:root /etc/nzyte-tv/secrets.env
sudo chmod 600 /etc/nzyte-tv/secrets.env
```

Its form is:

```text
NZYTE_TV_RTMP_URL=<SECRET>
```

Never put a real value in documentation or source control. FFmpeg receives the destination as an argument, so do not inspect full process command lines. Use `pgrep -x ffmpeg` for PID-only checks and `pgrep -x -c ffmpeg` for a count.

## Validate, run, and inspect

Validation does not start FFmpeg or mutate configuration:

```bash
/opt/nzyte-tv/app/nzytetv station validate \
  --config /etc/nzyte-tv/station.json
```

The destination line reports only `NOT CONFIGURED`, `CONFIGURED / VALID`, or `CONFIGURED / INVALID`. An absent destination does not prevent non-secret configuration/media validation because systemd can supply it later. A configured malformed destination makes validation `NOT READY`; `station run` requires a valid RTMP/RTMPS destination.

For a manual shell test, securely export the destination and run:

```bash
/opt/nzyte-tv/app/nzytetv station run \
  --config /etc/nzyte-tv/station.json

/opt/nzyte-tv/app/nzytetv station status
```

The default state path is `/var/lib/nzyte-tv/state.json`. An alternate state can be inspected with:

```bash
/opt/nzyte-tv/app/nzytetv station status --state /some/path/state.json
```

The 10-second heartbeat and 30-second stale threshold are unchanged. Status verifies the recorded station PID without displaying process arguments and never claims a queue match because `station status` does not load station configuration.

## Runtime-state schema version 2

Schema version 2 preserves the Checkpoint 1 process, current-item, FFmpeg, recovery, queue-count, error, stop, and completion fields. It adds:

- `queueId`: deterministic SHA-256 identity for the exact ordered validated queue;
- `queueItemCount`;
- zero-based `currentGlobalIndex`, `lastCompletedGlobalIndex`, and `resumeGlobalIndex`;
- `lastStartMode`, either `fresh` or `resume`;
- `resumeCount`; and
- `lastResumeAtUtc` after a cold resume.

The flattened global index crosses playlist boundaries. If playlist 1 has 325 items, its last item is global index 324 and playlist 2 sequence 1 is global index 325. CLI status presents one-based global positions or playlist/sequence details where the state can do so reliably.

`lastCompletedGlobalIndex` advances only after positive FFmpeg completion evidence. `resumeGlobalIndex` is the first item not positively known to be complete. While an item is active, resume points to that same item. At the first item, last-completed is null and resume is 0. After the final item completes, last-completed is the final index and resume is null.

State writes remain atomic: NZYTE TV writes and flushes a temporary file, then atomically moves it over the live file. A crash can therefore leave the previous valid JSON or the new valid JSON, not an in-place partial document. Ambiguous persistence biases toward replaying an item, never skipping unconfirmed content.

The deployed schema-version-1 state remains readable by `station status`. It has no trustworthy queue identity or completion cursor, so `station run` never guesses a cold-resume position from it. The first Checkpoint 2 run starts fresh and writes schema version 2; no manual JSON edit is required.

## Queue identity and start policy

`queueId` is a SHA-256 digest of an unambiguously framed representation containing the ordered playlist paths, hashes of the playlist content read during validation, and ordered validated broadcast-item identity. It changes when playlist order, playlist content, or material ordered item content changes. It does not use file modification timestamps, process IDs, the current time, runtime state, random values, or the RTMP/RTMPS destination.

At startup, NZYTE TV acquires an exclusive non-secret companion lock for the configured state path, validates configuration, builds the full plan, calculates the current queue ID, reads any prior state, checks for a live previous supervisor where appropriate, and applies this policy. The lock closes automatically on a process crash and prevents two near-simultaneous supervisors from writing one state file.

| Persisted state | Queue comparison | Result |
|---|---|---|
| No state file | Not applicable | Fresh at global index 0 |
| Schema version 1 | Not safely comparable | Fresh at global index 0; write schema version 2 |
| `completed` | Any | Explicit later start begins the configured queue fresh |
| `stopped` | Same queue | Resume saved `resumeGlobalIndex` |
| `stopped` | Different queue | Treat the changed programming as a fresh queue |
| `starting`, `broadcasting`, `stopping`, or `failed` with dead prior PID | Same queue | Resume saved item |
| Interrupted state | Different queue | Refuse startup; do not guess |
| Interrupted state with live prior station PID | Any | Refuse a second supervisor |
| Same queue but invalid/inconsistent cursor metadata | Same queue | Refuse unsafe automatic resume |

A clean Ctrl+C, `systemctl stop`, or normal reboot writes `stopped` without erasing queue identity, last-completed position, or resume position. Therefore `STOPPED` plus the same queue resumes. `STOPPED` plus changed programming intentionally starts the new queue at item 1. A successfully `COMPLETED` queue has no interrupted run; a later explicit start is a new fresh run.

Cold resume supplies the original zero-based global index to the existing `BroadcastRecoveryRunner`. Its concat slice omits earlier completed items and begins with the saved item, without `-ss` or timestamp seeking. Typed item-start/item-completion events retain original full-plan indices across both cold resume and internal FFmpeg retries.

## systemd installation and restart behavior

The service still runs as production user `u24`. From the repository checkout:

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

`StateDirectory=nzyte-tv` creates `/var/lib/nzyte-tv`; `StateDirectoryMode=0750` and `UMask=0027` restrict the generated state. The unit uses:

```text
Restart=on-failure
RestartSec=5
RestartPreventExitStatus=78
```

Exit code 78 means a permanent station-start/configuration or resume-safety failure. systemd does not loop on invalid configuration, a malformed destination, an unsafe cursor, a queue mismatch, or a detected second supervisor. Unexpected station runtime failure still exits 1 and remains eligible for restart. Successful completion and clean stop exit 0 and are not restarted.

Routine service controls are:

```bash
sudo systemctl start nzyte-tv
sudo systemctl status nzyte-tv
/opt/nzyte-tv/app/nzytetv station status
journalctl -u nzyte-tv -n 100
journalctl -u nzyte-tv -f

sudo systemctl stop nzyte-tv
pgrep -x ffmpeg
```

The final process check should print nothing. SIGTERM cancels heartbeat and recovery, terminates the owned FFmpeg child tree, starts no retry, and writes `stopped` while retaining the resume cursor.

Boot enablement remains an explicit operator decision. Installation and application code never run `systemctl enable`; the production operator enabled the service only after Tests A, B, and C passed.

## Raspberry Pi acceptance procedure and result

The following manual tests passed on the Raspberry Pi. Keep the procedure for later deployment regression checks; it is not an automated workstation test.

### Test A — hard parent-process crash

1. Start the service manually and let several items progress.
2. Record `station status`.
3. Get only the parent PID: `systemctl show -p MainPID --value nzyte-tv`.
4. Send SIGKILL only to that NZYTE TV parent: `sudo kill -KILL <parent-pid>`.
5. Let `Restart=on-failure` create a new parent.
6. Verify a new station PID, a new FFmpeg PID, restart of the interrupted item, no replay from sequence 1, `Start mode: RESUME`, and one increment to `Resume count`.

Do not use a command that displays FFmpeg arguments.

### Test B — clean stop and start

1. While an item is active, run `sudo systemctl stop nzyte-tv`.
2. Verify `STOPPED` and that `pgrep -x ffmpeg` prints nothing.
3. Run `sudo systemctl start nzyte-tv`.
4. Verify the same queue resumes the saved item instead of sequence 1.

### Test C — graceful reboot while still disabled

1. Run `systemctl is-enabled nzyte-tv` and confirm it reports `disabled`, then start the service manually.
2. Record the current item.
3. Run `sudo reboot`.
4. After the Pi returns, verify the service did not auto-start.
5. Start it manually with `sudo systemctl start nzyte-tv`.
6. Verify the saved item restarts from its beginning and earlier completed items are omitted.

This test proves graceful SIGTERM/reboot semantics, not stale-process recovery alone.

### Test D — final boot enablement

Only after A, B, and C pass:

```bash
sudo systemctl enable nzyte-tv
```

Reboot while the station is running. Verify automatic service startup, persisted queue resume, `pgrep -x -c ffmpeg` reports exactly `1`, the correct item is active, status is healthy, and no secret is disclosed. This operator action passed as the final Checkpoint 2 acceptance step; it was not performed by NZYTE TV or installation code.

## Current limitations

Checkpoint 2 is durable resume for the explicitly configured static queue, not unattended continuous programming. Checkpoint 3B1 can prepare future playlists before they are supplied to the station, but the station does not discover them, append to the active queue, or hand off between blocks. It does not mutate ordinary or rolling planned history, persist an exact media timestamp, call YouTube APIs, monitor remote stream health, or alert an operator. Scheduler history remains planned-programming history; station state remains actual runtime progress. See [rolling-programming.md](rolling-programming.md) for the deliberately separate planning side.
