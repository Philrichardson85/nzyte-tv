# Broadcasting generated playlists

The `broadcast` command streams one or more generated NZYTE TV playlist JSON files to an RTMP or RTMPS destination. It uses the normalized library directly with FFmpeg concat and stream-copy; it does not encode, filter, alter playlists, or modify media.

For the full novice workflow—including Pi mounting, candidate deployment, safe secret entry, tmux, and troubleshooting—start with the [operations runbook](operations-runbook.md).

## Prerequisites

- FFmpeg is installed and available on `PATH`.
- Every playlist uses supported playlist schema version 1.
- Every playlist item points to an existing normalized `.mp4` under the supplied library root.
- Each normalized file has its `.nzytetv.json` technical manifest.

Create playlists with `build-playlist` before broadcasting. The broadcaster never generates or repairs a playlist automatically.

## Destination secret

Set the destination in `NZYTE_TV_RTMP_URL`. Never put a real stream key in source control, a playlist, a command-line argument, documentation, or a screenshot.

On the Pi, avoid typing an `export ...="secret"` command that may be retained in shell history. Read the value silently instead:

Bash:

```bash
read -rsp "Paste full YouTube RTMPS URL: " NZYTE_TV_RTMP_URL
echo
export NZYTE_TV_RTMP_URL
```

`read` reads the value; `-r` uses raw input, `-s` hides pasted text, and `-p` displays the prompt. The URL conceptually ends with `/live2/<SECRET-KEY>`; never substitute a real key in documentation.

Check only whether the variable exists:

Bash:

```bash
if [ -n "$NZYTE_TV_RTMP_URL" ]; then
    echo "Broadcast destination configured"
else
    echo "Broadcast destination NOT configured"
fi
```

Never print the value of `NZYTE_TV_RTMP_URL`. The check above reports only whether a destination is configured. Run `unset NZYTE_TV_RTMP_URL` when the manual session is finished. A future supervised service should use service-level secret handling.

### Process-list safety

NZYTE TV redacts the RTMP/RTMPS destination from its application logs, but FFmpeg receives that destination as a process argument. A process command-line display can therefore expose the stream credential. Do not display FFmpeg command lines or print the destination variable while operating a live destination.

Use these safe checks instead:

```bash
pgrep -x ffmpeg
pgrep -x -c ffmpeg
```

`pgrep -x ffmpeg` prints only matching PID(s). `pgrep -x -c ffmpeg` prints only the count.

### Refresh a changed YouTube destination

Environment values are inherited when the broadcaster process starts. Changing `NZYTE_TV_RTMP_URL` in another shell does not update an already-running broadcaster. If YouTube changes the RTMPS destination or token, stop the broadcaster cleanly with Ctrl+C, then refresh the value in the shell that will start the replacement process:

```bash
unset NZYTE_TV_RTMP_URL
read -rsp "Paste full YouTube RTMPS URL: " NZYTE_TV_RTMP_URL
echo
export NZYTE_TV_RTMP_URL

/opt/nzyte-tv/app/nzytetv broadcast ...
```

Use `unset` when appropriate to clear an old shell value. Never include a real destination or stream key in a command, log, or screenshot.

## Validate without broadcasting

Dry-run parses every playlist, checks schema and item sequence, resolves each library-relative path, rejects traversal, confirms each MP4 and technical manifest exists, and verifies that FFmpeg is available. It does not require `NZYTE_TV_RTMP_URL`, create the temporary concat input, launch a stream, or write production state.

Bash:

```bash
nzytetv broadcast \
  /srv/nzyte-tv/media/playlists/production-01.json \
  /srv/nzyte-tv/media/playlists/production-02.json \
  --library /srv/nzyte-tv/media/library \
  --dry-run
```

Review the reported playlist count, scheduled items, duration, missing files, invalid paths, and unready assets. Proceed only when status is `READY`.

A historical acceptance dry-run validated two playlists, 650 scheduled items, approximately 12 hours, and zero missing, invalid, or unready assets. Those numbers describe one tested queue, not a permanent requirement.

## Start a broadcast

Bash:

```bash
nzytetv broadcast \
  /srv/nzyte-tv/media/playlists/production-01.json \
  /srv/nzyte-tv/media/playlists/production-02.json \
  --library /srv/nzyte-tv/media/library
```

Playlist files play in the supplied order. Items within each playlist play by their validated `sequence` value. FFmpeg reads one temporary concat input in real time and publishes FLV with `-c copy`; no video or audio encoder is used.

Press Ctrl+C to cancel. NZYTE TV cancels the child process, terminates its process tree, removes temporary concat files, and exits nonzero.

## RTMPS recovery

NZYTE TV watches FFmpeg's machine-readable `-progress pipe:1` output while it streams. An unexpected FFmpeg/RTMPS termination enters bounded recovery: it creates a fresh concat file and reconnects automatically. The retry delays are 2, 5, 10, 20, 30, then up to 60 seconds, with at most 10 consecutive recovery attempts.

Recovery starts at the beginning of the interrupted item. It does not attempt unsafe mid-GOP seeking, and it never restarts the entire supplied queue from item 1: items already reported complete are omitted from the retry concat input. This runtime position is held only in the broadcaster process; it never changes playlist JSON or `history.json`.

This behavior was added after a Raspberry Pi 4 acceptance stream ran for approximately 2 hours 45 minutes at real-time speed before the remote RTMPS peer reset the connection. Post-failure Pi and external-media checks remained healthy. That is historical acceptance evidence, not a standing operational requirement.

After five minutes of healthy streaming, the consecutive-recovery budget resets. Ctrl+C from the parent broadcaster is intentional cancellation, so it stops either FFmpeg or an in-progress retry delay promptly and does not trigger recovery. If the budget is exhausted, the command exits nonzero with the last active item and a sanitized FFmpeg diagnostic. The RTMP/RTMPS destination remains redacted in all forwarded diagnostics. Playlist history is not rewritten during broadcaster recovery.

Verify that FFmpeg stopped:

Bash:

```bash
pgrep -x ffmpeg
```

No output means no FFmpeg process remains. Do not use `kill -9` as the normal stop method.

## Adding content while a broadcast is running

> **You do not have to stop a live broadcast merely to prepare new content somewhere else. You must stop it before removing or disrupting the production USB it is reading.**

At startup, `broadcast` validates the exact playlist files named on its command line and creates one FFmpeg concat sequence. It does not dynamically watch `/srv/nzyte-tv/media/source`, `library`, or `playlists`. Consequently:

- media prepared on another computer or drive does not disturb the active stream;
- a new file copied to the production drive does not enter the active queue;
- a newly generated playlist does not replace or extend the active queue; and
- an active library file must not be replaced, moved, or removed while FFmpeg may read it.

If the single production USB must leave the Pi, attach to tmux, press Ctrl+C, and verify `pgrep -x ffmpeg` prints nothing. Then unmount it with `sudo umount /srv/nzyte-tv/media` and check with `findmnt /srv/nzyte-tv/media`. An `autofs`/`systemd-1` trigger may remain visible, but the real exFAT filesystem must not remain mounted. Do not access the mountpoint again before unplugging it.

Normalization and metadata processing do not modify an existing playlist. Generate future playlist JSON with the current `history.json` when new assets should enter rotation. That history is updated during playlist generation and represents planned scheduling; the broadcaster does not update it as each item actually finishes. Stopping midway through planned blocks and regenerating can therefore diverge from what aired. Prefer finishing a block or choosing a deliberate maintenance boundary.

After returning and validating the USB, silently configure `NZYTE_TV_RTMP_URL` again and start a new invocation containing the desired future queue:

Bash:

```bash
/opt/nzyte-tv/app/nzytetv broadcast \
  /srv/nzyte-tv/media/playlists/production-03.json \
  /srv/nzyte-tv/media/playlists/production-04.json \
  --library /srv/nzyte-tv/media/library
```

See [Adding media while NZYTE TV is running](operations-runbook.md#adding-media-while-nzyte-tv-is-running) for the complete Windows normalization, USB return, playlist generation, and restart procedure.

## Keep a manual broadcast alive across SSH disconnects

tmux keeps the terminal session alive when SSH disconnects, but it does not survive a Pi reboot or power loss.

Bash:

```bash
tmux new -s nzyte-tv
```

Inside tmux, enter the destination with `read -rsp` and start the broadcaster. Detach by pressing Ctrl+B, releasing the keys, then pressing D. Reconnect later with:

Bash:

```bash
tmux ls
tmux attach -t nzyte-tv
```

Attach and press Ctrl+C to stop the broadcaster. tmux protects a manual session from an SSH disconnect; broadcaster recovery separately protects the active connection from transient RTMPS/FFmpeg failures. Neither survives a reboot or replaces supervision. The broadcaster does not generate future playlists, dynamically reload its queue, restart after reboot, call the YouTube API, or monitor remote stream health. The current v0.4-era broadcaster uses the fixed queue supplied at startup.

## Historical v0.4.1 live acceptance evidence

This is historical acceptance evidence, not a permanent operating requirement or a guarantee that every future network failure is recoverable. The accepted Raspberry Pi candidate was freshly published as a self-contained `linux-arm64` build from commit `c23e461` ("Fix broadcast recovery bypass"). The Pi source checkout was explicitly verified at the full commit `c23e46168be883e4e857abda5ee23568dc3b7ba0`, and the candidate was demonstrably different from the previously deployed binary.

During a YouTube RTMPS live run, FFmpeg was deliberately terminated externally twice. NZYTE TV automatically launched a replacement FFmpeg process after each termination. YouTube briefly displayed loading while reconnecting, then resumed playback. The broadcaster remained stable afterward, and the two-playlist queue ran overnight and completed.

The final FFmpeg progress was approximately `out_time=12:00:43`, `bitrate=5833.5 kbits/s`, `speed=1x`, `dup_frames=0`, `drop_frames=0`, and `progress=end`. NZYTE TV reported `Broadcast status: COMPLETED`.

This real-world test validated multi-playlist sequential playback, FFmpeg stream-copy, RTMPS recovery, interrupted-item restart behavior, continued operation after recovery, long-duration stability, and normal completion. The already-published `v0.4.0` tag remains historical at the earlier broadcaster state; the recovery-corrected release is planned as `v0.4.1` after acceptance and documentation are complete. This note does not change tags or rewrite release history.

### Observed YouTube bitrate advisory

During this long-duration test, YouTube Studio at one point displayed a low-bitrate advisory of about `2812 Kbps` and recommended a higher ingest bitrate. Playback remained operational; the later/final FFmpeg output averaged approximately `5833.5 kbits/s` at `speed=1x`, with zero duplicate and dropped frames. The approximately 12-hour queue completed normally, and visual inspection on a large television showed no obvious quality problem.

The current decision is to **keep the existing normalization/broadcast profile for now**. This was an observed advisory, not a broadcaster failure; it does not establish that the current profile exactly meets every YouTube recommendation. Any future profile change should be evidence-driven by visible compression artifacts, sustained poor stream health, or another operational reason.
