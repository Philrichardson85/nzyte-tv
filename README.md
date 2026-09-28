# NZYTE TV

NZYTE TV is a production-validated media-preparation, programming, and broadcast automation system for prerecorded channels. It inspects source media, normalizes it to one deterministic broadcast format, independently verifies the result, builds deterministic playlists, and can stream generated playlists sequentially through FFmpeg.

Current development includes broadcast playback for generated playlist JSON using FFmpeg concat, real-time input pacing, stream-copy, an environment-supplied RTMP/RTMPS destination, and bounded reconnect recovery that restarts only an interrupted asset. Encoding remains independent from metadata, scheduling, and playback.

YouTube API integration, services, live playlist watching, queue regeneration, health polling, and automatic restarts remain out of scope.

## Operating NZYTE TV

Start with the [complete operations runbook](docs/operations-runbook.md). It takes a novice from a new Windows computer through portable-drive preparation, normalization, metadata, playlists, Raspberry Pi deployment, RTMPS broadcast, tmux operation, safe stopping, and troubleshooting.

Focused references:

- [Workstation setup](docs/workstation-setup.md)
- [Raspberry Pi setup and deployment](docs/raspberry-pi-setup.md)
- [Media library and portable-drive workflow](docs/media-library.md)
- [Content catalog and asset metadata](docs/content-catalog.md)
- [Playlist and programming engine](docs/playlists.md)
- [Broadcasting generated playlists](docs/broadcasting.md)

## Setup

Supported platforms:

- Windows x64
- Linux x64
- Linux ARM64, including Raspberry Pi

Prerequisites:

- Git
- .NET 10 SDK
- FFmpeg and FFprobe

New users should start with the [complete workstation setup guide](docs/workstation-setup.md). It includes Windows and Linux installation, stable release checkout, self-contained publishing, updates, portable media drives, and command-not-found troubleshooting.

Related guides:

- [Raspberry Pi setup and deployment](docs/raspberry-pi-setup.md)
- [Media library and portable-drive workflow](docs/media-library.md)
- [Complete operations runbook](docs/operations-runbook.md)

A tagged `vX.Y.Z` release is recommended for a reproducible production workstation; `main` is the latest development and integration state. Check the repository's available tags rather than assuming a version named in documentation is permanently latest.

## Verified Raspberry Pi deployment

The media-normalization workflow has been run successfully on:

- Raspberry Pi 4;
- Ubuntu Desktop 24.04.5 LTS, 64-bit;
- ARM64 / `aarch64`;
- FFmpeg and FFprobe `6.1.1-3ubuntu5`;
- .NET SDK `10.0.112`, host/runtime `10.0.12`;
- .NET RID `ubuntu.24.04-arm64`;
- OS storage at `/dev/mmcblk0p2`, ext4, approximately 29 GB;
- portable exFAT USB media mounted by UUID through systemd automount at `/srv/nzyte-tv/media`.

The verified deployment uses Xorg/X11 for incoming AnyDesk sessions. In the tested setup, an active physical monitor connected through the Raspberry Pi's micro-HDMI port is required for AnyDesk to display the desktop. A dummy HDMI adapter has not been tested.

For the complete blank-device procedure, see [Raspberry Pi 4 deployment guide](docs/raspberry-pi-setup.md).

## Dependencies

| Dependency | Purpose | Deployment requirement |
|---|---|---|
| .NET 10 SDK | Build, test, and publish | Required when building on the Pi or a developer machine |
| .NET runtime | Run framework-dependent builds | Included in the verified self-contained Pi publish |
| FFmpeg | Normalize media | Always external and must be on `PATH` |
| FFprobe | Inspect and verify media | Always external and must be on `PATH` |
| Git | Clone and update source | Required for source-based deployment |

NZYTE TV does not implement codecs in managed code and does not bundle FFmpeg or FFprobe.

FFmpeg and FFprobe are required by media inspection, normalization, and verification commands. The `metadata` command group does not require or invoke either tool.

## CLI

```text
nzytetv inspect <input>
nzytetv media init <media-root>
nzytetv normalize <input> [--overwrite] [--vertical-layout blurred-background]
nzytetv normalize-library <source-root> <destination-root> [--overwrite] [--vertical-layout blurred-background]
nzytetv verify <input>
nzytetv build-playlist <library-root> --catalog <catalog-path> --output <playlist-path> --duration <value> [--seed <integer>] [--history <history-path>] [--dry-run]
nzytetv broadcast <playlist> [<playlist> ...] --library <library-root> [--dry-run]
nzytetv metadata initialize <source-root> <library-root> --catalog <catalog-path> [--dry-run]
nzytetv metadata review <source-root> <library-root> --catalog <catalog-path>
nzytetv metadata sync <source-root> <library-root>
nzytetv metadata rebind <old-source-path> <new-source-path>
nzytetv metadata edit <source-media-path> --type <type> [--subtype <subtype>]
```

Use `--help` on the root command or any subcommand. Exit code `0` means success; invalid arguments, media failures, and failed verification return non-zero codes.

`normalize` writes `./BroadcastReady/<original-file-name>.mp4` relative to the current working directory. The source is never modified. An existing destination is rejected unless `--overwrite` is supplied, and that option still cannot replace the source. The final destination is published only after automatic verification succeeds. Video-only sources receive silent 48 kHz stereo AAC audio.

Portrait and 9:16 sources can be normalized explicitly with `--vertical-layout blurred-background`. The source remains sharp, centered, and undistorted at full 1080-pixel height while a cropped, blurred, darkened, slightly desaturated copy fills the 1920x1080 background. Display rotation metadata is honored. Landscape inputs supplied with the option still use the ordinary scale-and-pad path. Transformation and broadcast normalization happen in one encode and use the same verification as every other asset; the option is never enabled implicitly.

### Initialize a portable media root

`media init <media-root>` prepares a blank or partially populated removable drive without formatting it, copying media, running metadata, invoking FFmpeg/FFprobe, or normalizing anything. It creates missing `source` category folders, including `Visualizers` and `Animated Visuals`, plus `library`, `catalog`, `playlists`, and `work`, a minimal `.nzytetv-media-root.json` descriptor, and `catalog/song-catalog.json` when absent. Existing files and directories are never replaced or cleaned. Rerunning it on an existing portable root safely adds only missing category directories and preserves the descriptor, catalogs, media, manifests, metadata, playlists, and history.

```powershell
nzytetv media init "E:\"

nzytetv normalize-library `
  "E:\source" `
  "E:\library" `
  --vertical-layout blurred-background
```

Copy original masters into `E:\source\<category>\`, normalize on the workstation, then use the same physical drive on the Raspberry Pi. Source and library programming sidecars, technical manifests, the catalog, and optional playlists/history travel with the drive. Windows drive letters and Linux mount paths are not asset identity, so current normalized assets do not need another encode merely because `E:\` later mounts at `/srv/nzyte-tv/media`. The repository and executable remain installed on each host and do not need to live on the media drive. See [media-library.md](docs/media-library.md) for the complete workflow and directory layout.

### Normalize a library

`normalize-library` recursively discovers `.mp4`, `.mov`, and `.mkv` files using case-insensitive extension matching. It preserves each source-relative category path and changes the normalized output extension to `.mp4`:

```text
Source:      <source-root>/Music Videos/example.mov
Destination: <destination-root>/Music Videos/example.mp4
```

The source and destination roots must be separate and cannot be nested inside one another. `System Volume Information`, reparse-point/symbolic-link directories, images, text files, and unsupported extensions are ignored.

Each successful output has a sidecar source manifest named `<output>.mp4.nzytetv.json`. It records the source-relative path, source size, source last-modified UTC, manifest schema, broadcast-profile version, and requested vertical-layout mode.

Without `--overwrite`, an existing destination is skipped only when its source manifest matches the current source and independent verification passes. A missing, corrupt, stale, or profile-mismatched manifest forces re-normalization. A matching destination that fails verification is also re-normalized. With `--overwrite`, only the destination is replaced, and only after the new temporary output passes verification.

One failed file does not stop later files. The final summary reports discovered, normalized, verified-skipped, failed, and verified-ready counts. Any per-file failure makes the command exit nonzero. Ctrl+C cancels the active FFmpeg process and does not publish its partial output.

For bulk social-media ingest, keep portrait masters under `source/` and run `normalize-library ... --vertical-layout blurred-background`; relative folders are preserved under `library/`. This replaces the former separate PowerShell `16x9 Output` conversion for normal NZYTE TV ingest. Custom hand-designed 16:9 exports can still be placed in `source/` and normalized normally without the option. The former FFmpeg filter recipe remains documented as a manual reference in [media-library.md](docs/media-library.md).

### Content catalog and programming metadata

The versioned master song catalog provides stable `contentGroupId` values for songs. Each source video receives its own stable `assetId` in a `.nzytetv.meta.json` programming sidecar. This is separate from the existing `.nzytetv.json` technical normalization manifest.

`metadata initialize` detects asset types from source categories and known short-form filename descriptors, preserves existing metadata, creates new identities, conservatively matches song titles or aliases, reports ambiguous and unresolved assets, and synchronizes metadata to existing normalized library files. `--dry-run` writes nothing. `metadata review` records an explicit human choice without changing `assetId`; `metadata edit` overrides a folder-derived type without moving or encoding media; `metadata sync` propagates programming changes without encoding; `metadata rebind` preserves identity after an intentional source rename.

Duplicate song titles are supported because relationships use `contentGroupId`, never title alone. Filenames help discovery; after resolution, the catalog supplies the canonical song `title` and `artist` while the sidecar remains authoritative for asset identity and programming fields. Visualizers (full-song static or lightly animated graphical presentations) and animated visuals (animated, narrative, or cinematic song presentations) are song-based assets and share the same `contentGroupId` clock as every other presentation of the recording. See [content-catalog.md](docs/content-catalog.md) for schemas, matching rules, review, synchronization, eligibility, and rename behavior.

### Playlist generation

`build-playlist` takes a read-only snapshot of eligible normalized library assets, discovers actual durations with FFprobe, and schedules whole assets until the requested duration is reached or exceeded. The provisional six-hour default mix is 20% music-video, 15% lyric-video, 15% visualizer, 20% animated-visual, 10% performance, 5% short-form, and 15% vlog by airtime. A fixed seed makes ordering reproducible, and bounded history carries exact-asset and same-song cooldowns across playlist files. Song-based normal assets at or below 60 seconds (and all short-form assets) use short-presentation pacing: short-to-short prefers 15 minutes with a 10-minute floor, full-to-short prefers 30 minutes with a 20-minute floor, and short-to-full prefers 30 minutes with a 15-minute floor. Full-to-full keeps its established 90/60/45-minute policy.

The current small production library cannot satisfy every ideal rule for six hours. The engine therefore relaxes category targeting, exact-asset cooldown, hot preference, and single-vlog pacing before relaxing the preferred 90-minute same-song target. A controlled song relaxation may use the 60–90-minute range. Before creating vlog #3, a music-first rescue may use a song in the 45–60-minute range; sub-45-minute repeats and still-unavoidable vlog runs longer than two are explicit emergency violations. Promo, interstitial, and bumper minimum cadence spacing remains a separate eligibility invariant, so these assets cannot become generic fallback filler. Every relaxation, cadence insertion, cadence miss, and exclusion is reported. See [playlists.md](docs/playlists.md) for policy defaults, JSON schemas, history behavior, and dry-run usage.

### Broadcast playback

`broadcast` validates one or more generated schema-version-1 playlists and plays them sequentially from the normalized library. It uses an FFmpeg concat input with real-time pacing, `-c copy`, and FLV output, so playback does not re-encode or filter media. Library-relative paths are resolved safely beneath the supplied root; missing MP4 files, missing technical manifests, traversal attempts, malformed playlists, and invalid sequences prevent broadcast startup.

The RTMP/RTMPS destination is read only from `NZYTE_TV_RTMP_URL` and is never displayed. `--dry-run` does not require the variable and does not launch FFmpeg. Pressing Ctrl+C cancels and terminates the FFmpeg child process. See [broadcasting.md](docs/broadcasting.md) for setup and usage.

## Broadcast standard

Broadcast-ready files must pass all of these checks:

- usable MP4 container;
- H.264 High-profile video;
- 1920x1080 resolution;
- constant 30 fps;
- `yuv420p` pixel format;
- approximately two seconds between actual consecutive keyframes;
- AAC audio;
- 48 kHz sample rate;
- two audio channels.

See [broadcast-standard.md](docs/broadcast-standard.md) for encoding settings, tolerances, operational background, and the verified Lady Lady before/after reference case.

## Production deployment layout

```text
/opt/nzyte-tv/
|-- src/       repository checkout
`-- app/       published production application

/srv/nzyte-tv/
|-- logs/
|-- playlists/
|-- catalog/                       versioned programming catalog
|-- work/
|-- media/                         external USB mount
|   |-- source/                    original/master media
|   |-- library/                   normalized and verified assets
|   `-- System Volume Information/ Windows filesystem metadata; ignore
|-- source -> /srv/nzyte-tv/media/source
`-- library -> /srv/nzyte-tv/media/library
```

The verified executable is `/opt/nzyte-tv/app/nzytetv`. It is not named `NzyteTv.Cli`. The SD card should primarily hold Ubuntu, the application, repository, catalog, logs, playlists, and working data. Large source and broadcast media belong on the external USB drive. Application commands should use the convenience paths `/srv/nzyte-tv/source` and `/srv/nzyte-tv/library`.

## Raspberry Pi deployment

The [Raspberry Pi setup guide](docs/raspberry-pi-setup.md) contains the complete blank-device procedure, external-drive mount, stable release checkout, ARM64 publish, candidate/rollback deployment, and acceptance commands. Shared Git, .NET, FFmpeg, update, and publishing concepts live in the [workstation setup guide](docs/workstation-setup.md).

## Raspberry Pi acceptance results

NZYTE TV v0.1 completed real-media acceptance testing on a Raspberry Pi 4 Model B with 4 GB RAM, Ubuntu, and packaged FFmpeg/FFprobe.

A representative production batch contained five music videos and four vlog episodes:

| Result | First run | Unchanged second run |
|---|---:|---:|
| Discovered | 9 | 9 |
| Normalized | 9 | 0 |
| Skipped existing | 0 | 9 |
| Failed | 0 | 0 |
| Verified ready | 9 | 9 |
| Elapsed | 01:31:57 | 00:00:57 |

Every first-run output completed normalization and independent verification. On the second run, all nine existing destinations were re-verified and skipped correctly. These measurements were recorded before source manifests were introduced. After upgrading to the manifest-aware build, pre-manifest outputs are normalized once more to establish a trusted source/output association.

Three normalized files were then streamed manually from the Pi to YouTube Live with FFmpeg stream-copy (`-c copy`) and FLV over RTMPS:

1. `Nzyte - CASH RULES (Official Music Video).mp4`
2. `Nzyte Vlog Episode 3.mp4`
3. `Nzyte - American Dreams (Official Video).mp4`

All three displayed with correct audio and aspect ratio, maintained A/V synchronization, ran at approximately `speed=1.00x`, and received YouTube **Excellent** stream health. This validates the prepared-media path through normalization, verification, Raspberry Pi stream-copy, and YouTube ingest. The `broadcast` command now automates that stream-copy path; YouTube API integration remains out of scope.

### Live FLV shutdown warning

When a manually operated live FLV/RTMP stream is stopped, FFmpeg can print:

```text
Failed to update header with correct duration.
Failed to update header with correct filesize.
```

These warnings are harmless in this live-stream shutdown context: a live, non-seekable output cannot have its header rewritten like a completed local file. The broadcaster uses this option to suppress the expected warnings:

```text
-flvflags no_duration_filesize
```

The current `broadcast` command automates this validated stream-copy path for generated playlists without adding YouTube API integration.

## Build, update, and publish

The [workstation setup guide](docs/workstation-setup.md) provides copy/paste PowerShell and Bash instructions for cloning a stable tag, tracking `main`, updating with `--ff-only`, and publishing self-contained `win-x64`, `linux-x64`, or `linux-arm64` applications. FFmpeg and FFprobe remain external dependencies even with a self-contained publish.

## Architecture and testing

- `NzyteTv.Cli` owns argument handling and console presentation.
- `NzyteTv.Core` owns domain models, output safety, rational-number handling, broadcast validation, catalog identity, matching, category, metadata-validation, eligibility, scheduling policy, cooldowns, relaxation, and playlist/history models. It has no FFmpeg dependency.
- `NzyteTv.Media` owns tool discovery, asynchronous process execution, typed FFprobe JSON parsing, normalization and verification orchestration, broadcast-plan filesystem validation, concat generation, stream-copy execution, read-only playlist library snapshots, duration inspection, and JSON/filesystem adapters.

Tests cover command parsing, recursive library discovery, extension filtering, relative path preservation, resumability, failure continuation, output paths and overwrite protection, rational frame rates, FFprobe JSON, FFmpeg arguments, normalization publication behavior, broadcast rules, cancellation, keyframe intervals, catalog validation, matching ambiguity, metadata idempotence, dry-run safety, review, synchronization, rename/rebind, orphan reporting, and playlist eligibility. The integration test creates a tiny clip at runtime when FFmpeg and FFprobe are available and skips otherwise. No test media is committed.

## Further documentation

- [Windows, Linux, and Raspberry Pi workstation setup](docs/workstation-setup.md)
- [Raspberry Pi setup and deployment](docs/raspberry-pi-setup.md)
- [Broadcast standard](docs/broadcast-standard.md)
- [Media library and normalization workflow](docs/media-library.md)
- [Content catalog and asset metadata](docs/content-catalog.md)
- [Playlist and programming engine](docs/playlists.md)
- [Broadcasting generated playlists](docs/broadcasting.md)
- [Complete operations runbook](docs/operations-runbook.md)
