# Media library and normalization workflow

This document records the mounted media library observed on the Raspberry Pi and defines safe handling rules for future media-scanning work.

## Storage location

The SanDisk Cruzer Glide NTFS partition is mounted at:

```text
/srv/nzyte-tv/media
```

Its persistent mount and recovery procedure are documented in [raspberry-pi-setup.md](raspberry-pi-setup.md). Do not format this drive during application setup.

## Verified category folders

```text
/srv/nzyte-tv/media/
|-- Advertisements/
|-- Bumpers/
|-- Interstitials/
|-- Lyric Videos/
|-- Music Videos/
|-- Promos/
|-- Specials/
`-- Vlog Episodes/
```

Folder names, spaces, and capitalization are intentional. Code and operational commands must preserve them exactly.

Because the drive is NTFS and has been used with Windows, this directory may also appear:

```text
System Volume Information
```

It is filesystem metadata, not a media category. Ignore it; do not inspect, normalize, move, or delete it.

## Verified inventory

At the time of the Raspberry Pi deployment review, the library contained 39 video files:

- all 39 inspected files used H.264 video and `yuv420p`;
- 31 files were 24 fps;
- 8 files were 30 fps;
- 17 files were 1920x1080;
- 21 files were 2560x1440;
- 1 file was 2628x1440.

The 2628x1440 file is:

```text
Vlog 2 episode 3.mp4
```

It is a useful future scale-and-pad regression case because its aspect ratio does not exactly match 1920x1080. That specific regression test has not yet been automated.

## Media-file filtering

The `normalize-library` command provides the first recursive media scanner. Its current case-insensitive allowlist is:

```text
.mp4
.mov
.mkv
```

It does not assume that every file or directory beneath a category is playable media. Current scanner behavior:

- enumerate only configured category directories;
- skip `System Volume Information` and other non-category directories;
- filter candidate files by the explicit, case-insensitive allowlist above;
- preserve the original full path, filename, spaces, capitalization, and Unicode;
- send candidates through `inspect` and then normalization/verification rather than trusting an extension;
- continue safely when an unsupported or unreadable file is encountered.

`.mp4` is verified with the current Raspberry Pi library and workflow. `.mov` and `.mkv` discovery and destination mapping are covered by automated tests, but representative Pi library normalization runs for those source containers have not yet been recorded. Add extensions only with representative tests.

## Source and BroadcastReady files

Mounted library files are source media. Keep them unchanged.

The current verified workflow uses local working storage:

```text
/srv/nzyte-tv/work/
`-- BroadcastReady/
```

Run normalization from `/srv/nzyte-tv/work`:

```bash
cd /srv/nzyte-tv/work
/opt/nzyte-tv/app/nzytetv normalize \
  "/srv/nzyte-tv/media/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4"
```

The result is:

```text
/srv/nzyte-tv/work/BroadcastReady/Lady Lady - Nzyte (Official Music Video).mp4
```

Normalization writes to a temporary output, verifies it, and publishes the destination only when verification passes. `--overwrite` replaces an existing destination only; it never replaces the source.

Treat a file as broadcast ready only after either:

- `normalize` completes and its automatic verification reports `RESULT: BROADCAST READY`; or
- an independent `verify` command reports the same result.

For the entire library, use:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /srv/nzyte-tv/media \
  /srv/nzyte-tv/work/BroadcastReady
```

This creates paths such as:

```text
/srv/nzyte-tv/work/BroadcastReady/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4
/srv/nzyte-tv/work/BroadcastReady/Vlog Episodes/Vlog 2 episode 3.mp4
```

The batch workflow has these safety and resumability rules:

- source and destination roots must be separate and non-overlapping;
- relative category directories are preserved;
- `.mov` and `.mkv` destination extensions become `.mp4`;
- each successful destination receives a `<output>.mp4.nzytetv.json` source-manifest sidecar;
- a destination is skipped only when its source fingerprint matches and independent verification passes;
- a changed source, changed broadcast profile, missing/corrupt manifest, or invalid destination causes re-normalization;
- failures are recorded without aborting later files;
- colliding source names such as `name.mov` and `name.mkv` in one category fail rather than overwriting the same `name.mp4` destination;
- Ctrl+C cancels the active file, while temporary-output handling prevents a partial final destination.

Promotion from the work directory into a future permanent broadcast library is not implemented in v0.1. Do not describe a manual copy as an application feature.

## Quoting paths on Linux

Always quote paths because category and media names contain spaces:

```bash
/opt/nzyte-tv/app/nzytetv inspect \
  "/srv/nzyte-tv/media/Vlog Episodes/Vlog 2 episode 3.mp4"
```

The application passes paths directly to FFmpeg and FFprobe without constructing a shell command. Scripts that call the application must still quote each shell argument correctly.

## Normalization checklist

1. Confirm the USB drive is mounted by accessing `/srv/nzyte-tv/media` and running `findmnt`.
2. Inspect the source with its complete quoted path.
3. Change to `/srv/nzyte-tv/work`.
4. Run `normalize` without `--overwrite` for a new output.
5. Review the automatic verification report.
6. Run `verify` independently when performing an acceptance test.
7. Retain the source unchanged.
8. Do not treat failed or partial output as library-ready media.

For an unattended batch, run `normalize-library`, retain its final summary, investigate every listed failure, and rerun without `--overwrite` to resume. A representative nine-file Raspberry Pi batch took 01:31:57; the unchanged second run re-verified and skipped all nine files in 00:00:57. A full 39-file batch has not yet been timed.

## Source manifests and migration

A source manifest records:

- manifest schema version;
- source-relative path;
- source size;
- source last-modified UTC;
- broadcast-profile version.

The manifest is written atomically after the output passes normalization and verification. If the source changes during normalization, the batch records a failure and does not mark that output as current.

Outputs created by older versions do not have manifests. The first run after upgrading conservatively normalizes those files again and creates sidecars. The application does not adopt an existing output based only on technical validity because it cannot prove that the output came from the current source master.

The nine-file acceptance run occurred before manifests were implemented. Its second-run skip result validated the earlier verify-and-skip behavior; the next run with the manifest-aware version will intentionally rebuild those nine outputs once.

The required format is defined in [broadcast-standard.md](broadcast-standard.md).
