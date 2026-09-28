# Broadcasting generated playlists

The `broadcast` command streams one or more generated NZYTE TV playlist JSON files to an RTMP or RTMPS destination. It uses the normalized library directly with FFmpeg concat and stream-copy; it does not encode, filter, alter playlists, or modify media.

## Prerequisites

- FFmpeg is installed and available on `PATH`.
- Every playlist uses supported playlist schema version 1.
- Every playlist item points to an existing normalized `.mp4` under the supplied library root.
- Each normalized file has its `.nzytetv.json` technical manifest.

Create playlists with `build-playlist` before broadcasting. The broadcaster never generates or repairs a playlist automatically.

## Destination secret

Set the destination in `NZYTE_TV_RTMP_URL`. Never put a real stream key in source control, a playlist, a command-line argument, or documentation.

Bash example with a placeholder:

```bash
export NZYTE_TV_RTMP_URL='rtmps://example.invalid/live2/<STREAM-KEY>'
```

PowerShell example with a placeholder:

```powershell
$env:NZYTE_TV_RTMP_URL = 'rtmps://example.invalid/live2/<STREAM-KEY>'
```

For unattended production use, supply the variable through the host's secret-management or service environment configuration. The command reports only whether a destination is configured and never displays its value.

## Validate without broadcasting

Dry-run parses every playlist, checks schema and item sequence, resolves each library-relative path, rejects traversal, confirms each MP4 and technical manifest exists, and verifies that FFmpeg is available. It does not require `NZYTE_TV_RTMP_URL`, create the temporary concat input, launch a stream, or write production state.

```bash
nzytetv broadcast \
  /srv/nzyte-tv/playlists/production-01.json \
  /srv/nzyte-tv/playlists/production-02.json \
  --library /srv/nzyte-tv/library \
  --dry-run
```

Review the reported playlist count, scheduled items, duration, missing files, invalid paths, and unready assets. Proceed only when status is `READY`.

## Start a broadcast

```bash
nzytetv broadcast \
  /srv/nzyte-tv/playlists/production-01.json \
  /srv/nzyte-tv/playlists/production-02.json \
  --library /srv/nzyte-tv/library
```

Playlist files play in the supplied order. Items within each playlist play by their validated `sequence` value. FFmpeg reads one temporary concat input in real time and publishes FLV with `-c copy`; no video or audio encoder is used.

Press Ctrl+C to cancel. NZYTE TV cancels the child process, terminates its process tree, removes the temporary concat file where practical, and exits nonzero. A nonzero FFmpeg exit also makes the command fail.
