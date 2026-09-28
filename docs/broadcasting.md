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

Never run `echo "$NZYTE_TV_RTMP_URL"`. The command reports only whether a destination is configured and never displays its value. Run `unset NZYTE_TV_RTMP_URL` when the manual session is finished. A future supervised service should use service-level secret handling.

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

Press Ctrl+C to cancel. NZYTE TV cancels the child process, terminates its process tree, removes the temporary concat file where practical, and exits nonzero. A nonzero FFmpeg exit also makes the command fail.

Verify that FFmpeg stopped:

Bash:

```bash
pgrep -a ffmpeg
```

No output means no FFmpeg process remains. Do not use `kill -9` as the normal stop method.

## Adding content while a broadcast is running

> **You do not have to stop a live broadcast merely to prepare new content somewhere else. You must stop it before removing or disrupting the production USB it is reading.**

At startup, `broadcast` validates the exact playlist files named on its command line and creates one FFmpeg concat sequence. It does not dynamically watch `/srv/nzyte-tv/media/source`, `library`, or `playlists`. Consequently:

- media prepared on another computer or drive does not disturb the active stream;
- a new file copied to the production drive does not enter the active queue;
- a newly generated playlist does not replace or extend the active queue; and
- an active library file must not be replaced, moved, or removed while FFmpeg may read it.

If the single production USB must leave the Pi, attach to tmux, press Ctrl+C, and verify `pgrep -a ffmpeg` prints nothing. Then unmount it with `sudo umount /srv/nzyte-tv/media` and check with `findmnt /srv/nzyte-tv/media`. An `autofs`/`systemd-1` trigger may remain visible, but the real exFAT filesystem must not remain mounted. Do not access the mountpoint again before unplugging it.

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

Attach and press Ctrl+C to stop the broadcaster. tmux is the current manual bridge; the broadcaster does not yet run as a systemd service, generate future playlists, track live playback position, dynamically reload its queue, restart after reboot, requeue failures, call the YouTube API, or monitor remote stream health. A later unattended version may add automatic queue advancement, future-block generation, maintenance-friendly ingestion, runtime playback state, and systemd operation. The current v0.4-era broadcaster uses the fixed queue supplied at startup.
