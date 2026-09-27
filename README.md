# NZYTE TV

NZYTE TV v0.1.0 is the production-validated media-preparation foundation for a future 24/7 prerecorded YouTube broadcast system. It inspects source media, normalizes it to one deterministic broadcast format, and independently verifies the result, including actual keyframe timestamps. The v0.1 media-normalization and verification milestone completed production acceptance on a Raspberry Pi 4 with the full 39-video library.

v0.2 development adds the content catalog and programming-metadata foundation. Encoding remains independent from metadata resolution: media can normalize and verify while its song relationship is unresolved, and metadata operations never invoke FFmpeg or re-encode video.

Streaming, scheduling, playlist selection, YouTube integration, services, and automatic restarts remain out of scope.

Windows x64 and Linux ARM64 remain first-class application targets; the verified production-style deployment is the Raspberry Pi environment below.

## Verified Raspberry Pi deployment

The media-normalization workflow has been run successfully on:

- Raspberry Pi 4;
- Ubuntu Desktop 24.04.5 LTS, 64-bit;
- ARM64 / `aarch64`;
- FFmpeg and FFprobe `6.1.1-3ubuntu5`;
- .NET SDK `10.0.112`, host/runtime `10.0.12`;
- .NET RID `ubuntu.24.04-arm64`;
- OS storage at `/dev/mmcblk0p2`, ext4, approximately 29 GB;
- external media storage at `/dev/sda1`, NTFS3, approximately 30 GB, mounted through systemd automount at `/srv/nzyte-tv/media`.

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
nzytetv normalize <input> [--overwrite]
nzytetv normalize-library <source-root> <destination-root> [--overwrite]
nzytetv verify <input>
nzytetv metadata initialize <source-root> <library-root> --catalog <catalog-path> [--dry-run]
nzytetv metadata review <source-root> <library-root> --catalog <catalog-path>
nzytetv metadata sync <source-root> <library-root>
nzytetv metadata rebind <old-source-path> <new-source-path>
nzytetv metadata edit <source-media-path> --type <type> [--subtype <subtype>]
```

Use `--help` on the root command or any subcommand. Exit code `0` means success; invalid arguments, media failures, and failed verification return non-zero codes.

`normalize` writes `./BroadcastReady/<original-file-name>.mp4` relative to the current working directory. The source is never modified. An existing destination is rejected unless `--overwrite` is supplied, and that option still cannot replace the source. The final destination is published only after automatic verification succeeds. Video-only sources receive silent 48 kHz stereo AAC audio.

### Normalize a library

`normalize-library` recursively discovers `.mp4`, `.mov`, and `.mkv` files using case-insensitive extension matching. It preserves each source-relative category path and changes the normalized output extension to `.mp4`:

```text
Source:      <source-root>/Music Videos/example.mov
Destination: <destination-root>/Music Videos/example.mp4
```

The source and destination roots must be separate and cannot be nested inside one another. `System Volume Information`, reparse-point/symbolic-link directories, images, text files, and unsupported extensions are ignored.

Each successful output has a sidecar source manifest named `<output>.mp4.nzytetv.json`. It records the source-relative path, source size, source last-modified UTC, manifest schema, and broadcast-profile version.

Without `--overwrite`, an existing destination is skipped only when its source manifest matches the current source and independent verification passes. A missing, corrupt, stale, or profile-mismatched manifest forces re-normalization. A matching destination that fails verification is also re-normalized. With `--overwrite`, only the destination is replaced, and only after the new temporary output passes verification.

One failed file does not stop later files. The final summary reports discovered, normalized, verified-skipped, failed, and verified-ready counts. Any per-file failure makes the command exit nonzero. Ctrl+C cancels the active FFmpeg process and does not publish its partial output.

### Content catalog and programming metadata

The versioned master song catalog provides stable `contentGroupId` values for songs. Each source video receives its own stable `assetId` in a `.nzytetv.meta.json` programming sidecar. This is separate from the existing `.nzytetv.json` technical normalization manifest.

`metadata initialize` detects asset types from source categories and known short-form filename descriptors, preserves existing metadata, creates new identities, conservatively matches song titles or aliases, reports ambiguous and unresolved assets, and synchronizes metadata to existing normalized library files. `--dry-run` writes nothing. `metadata review` records an explicit human choice without changing `assetId`; `metadata edit` overrides a folder-derived type without moving or encoding media; `metadata sync` propagates programming changes without encoding; `metadata rebind` preserves identity after an intentional source rename.

Duplicate song titles are supported because relationships use `contentGroupId`, never title alone. Filenames help discovery; after resolution, the catalog supplies the canonical song `title` and `artist` while the sidecar remains authoritative for asset identity and programming fields. See [content-catalog.md](docs/content-catalog.md) for schemas, matching rules, review, synchronization, eligibility, and rename behavior.

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
|   `-- System Volume Information/ Windows/NTFS metadata; ignore
|-- source -> /srv/nzyte-tv/media/source
`-- library -> /srv/nzyte-tv/media/library
```

The verified executable is `/opt/nzyte-tv/app/nzytetv`. It is not named `NzyteTv.Cli`. The SD card should primarily hold Ubuntu, the application, repository, catalog, logs, playlists, and working data. Large source and broadcast media belong on the external USB drive. Application commands should use the convenience paths `/srv/nzyte-tv/source` and `/srv/nzyte-tv/library`.

## Raspberry Pi quick-start deployment

This quick start assumes Ubuntu Desktop 24.04.5 ARM64 is already installed, SSH is configured, FFmpeg is installed, .NET 10 is installed, and the USB media drive is mounted. Use the [full Raspberry Pi setup guide](docs/raspberry-pi-setup.md) when starting from a blank Pi.

Create the directory layout:

```bash
sudo mkdir -p /opt/nzyte-tv/src /opt/nzyte-tv/app
sudo mkdir -p /srv/nzyte-tv/work /srv/nzyte-tv/logs /srv/nzyte-tv/playlists /srv/nzyte-tv/catalog
sudo chown -R "$USER":"$USER" /opt/nzyte-tv
sudo chown -R "$USER":"$USER" /srv/nzyte-tv/work /srv/nzyte-tv/logs /srv/nzyte-tv/playlists /srv/nzyte-tv/catalog
```

After mounting the external drive at `/srv/nzyte-tv/media`, create the permanent media directories and convenience links on a blank deployment:

```bash
mkdir -p /srv/nzyte-tv/media/source /srv/nzyte-tv/media/library
sudo ln -s /srv/nzyte-tv/media/source /srv/nzyte-tv/source
sudo ln -s /srv/nzyte-tv/media/library /srv/nzyte-tv/library
```

Clone, restore, build, and test:

```bash
git clone https://github.com/Philrichardson85/nzyte-tv.git /opt/nzyte-tv/src
cd /opt/nzyte-tv/src
dotnet restore NzyteTv.slnx
dotnet build NzyteTv.slnx --configuration Release --no-restore
dotnet test NzyteTv.slnx --configuration Release --no-build --no-restore
```

Publish the CLI project only:

```bash
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o /opt/nzyte-tv/app
```

Do not publish the whole solution to a shared `-o` directory. That was observed to produce `NETSDK1194` and mix application and test outputs.

Validate the application:

```bash
/opt/nzyte-tv/app/nzytetv --help
/opt/nzyte-tv/app/nzytetv inspect \
  "/srv/nzyte-tv/source/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4"
```

Run normalization from the desired working directory:

```bash
cd /srv/nzyte-tv/work
/opt/nzyte-tv/app/nzytetv normalize \
  "/srv/nzyte-tv/source/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4"
/opt/nzyte-tv/app/nzytetv verify \
  "/srv/nzyte-tv/work/BroadcastReady/Lady Lady - Nzyte (Official Music Video).mp4"
```

The verified normalization and automatic verification took 13 minutes 38 seconds on the Raspberry Pi 4. The independent verification reported `RESULT: BROADCAST READY`.

That was a temporary acceptance-test output. The test media was later removed from `/srv/nzyte-tv/work`, which now contains only small reference/test metadata files. Permanent production outputs belong in `/srv/nzyte-tv/library` through the batch workflow below.

For a complete library run, the production command is:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library
```

The production source library contains 39 videos: 14 lyric videos, 5 music videos, and 20 vlog-related videos. Its source size is approximately 7.3 GB and its combined runtime is 1:17:09 (4,630 seconds). The other category folders intentionally remain empty for future station programming.

The full 39-file production normalization completed successfully on the Raspberry Pi 4:

| Result | First run | Unchanged second run |
|---|---:|---:|
| Discovered | 39 | 39 |
| Normalized | 39 | 0 |
| Skipped existing | 0 | 39 |
| Failed | 0 | 0 |
| Verified ready | 39 | 39 |
| Manifests | 39 | 39 existing |
| Elapsed | 06:21:07 | 00:04:42 |

All 39 source files were normalized and independently verified. On the unchanged second run, all 39 matching destinations were re-verified and skipped, confirming resumability and manifest-based source matching for the production library. Final validation found 39 broadcast-ready videos and 39 matching source manifests in a 3.3 GB normalized library.

After normalization, the 30 GB external drive reported 11 GB used, 19 GB available, and 36% utilization. The production source and normalized library remain permanently stored beneath `/srv/nzyte-tv/media`, with `/srv/nzyte-tv/source` and `/srv/nzyte-tv/library` as their stable convenience symlinks. This completes the v0.1 media-normalization and verification milestone as production-validated on Raspberry Pi 4 / `linux-arm64`.

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

All three displayed with correct audio and aspect ratio, maintained A/V synchronization, ran at approximately `speed=1.00x`, and received YouTube **Excellent** stream health. This validates the prepared-media path through normalization, verification, Raspberry Pi stream-copy, and YouTube ingest. It does not add broadcasting or YouTube integration to the application.

### Live FLV shutdown warning

When a manually operated live FLV/RTMP stream is stopped, FFmpeg can print:

```text
Failed to update header with correct duration.
Failed to update header with correct filesize.
```

These warnings are harmless in this live-stream shutdown context: a live, non-seekable output cannot have its header rewritten like a completed local file. Future broadcaster commands may use this option to suppress the expected warnings:

```text
-flvflags no_duration_filesize
```

No broadcaster command is implemented in v0.1.

## Windows development setup

From a clean Windows x64 machine, run these commands in PowerShell or install the equivalent products from their official installers.

1. Install Git, .NET 10, and FFmpeg:

   ```powershell
   winget install --id Git.Git -e
   winget install --id Microsoft.DotNet.SDK.10 -e
   winget install --id Gyan.FFmpeg -e
   ```

2. Close and reopen PowerShell, then verify every dependency:

   ```powershell
   git --version
   dotnet --version
   ffmpeg -version
   ffprobe -version
   ```

   `dotnet --version` must begin with `10.`.

3. Clone, restore, build, and test:

   ```powershell
   git clone https://github.com/Philrichardson85/nzyte-tv.git nzyte_tv
   Set-Location nzyte_tv
   dotnet restore NzyteTv.slnx
   dotnet build NzyteTv.slnx --no-restore
   dotnet test NzyteTv.slnx --no-build --no-restore
   ```

4. Exercise each command:

   ```powershell
   dotnet run --project src/NzyteTv.Cli -- inspect "C:\Media\video.mp4"
   dotnet run --project src/NzyteTv.Cli -- normalize "C:\Media\video.mp4"
   dotnet run --project src/NzyteTv.Cli -- normalize-library "C:\Media" "C:\NZYTE\BroadcastReady"
   dotnet run --project src/NzyteTv.Cli -- verify ".\BroadcastReady\video.mp4"
   ```

## Publishing self-contained builds

The release workflow produces self-contained builds for `win-x64`, `linux-x64`, and `linux-arm64`. Publish one project and one runtime per output directory:

```powershell
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-arm64
```

Self-contained publishing includes the .NET runtime. FFmpeg and FFprobe remain external dependencies.

## Architecture and testing

- `NzyteTv.Cli` owns argument handling and console presentation.
- `NzyteTv.Core` owns domain models, output safety, rational-number handling, broadcast validation, catalog identity, matching, category, metadata-validation, and eligibility rules. It has no FFmpeg dependency.
- `NzyteTv.Media` owns tool discovery, asynchronous process execution, typed FFprobe JSON parsing, normalization and verification orchestration, and JSON/filesystem adapters for catalogs and sidecars.

Tests cover command parsing, recursive library discovery, extension filtering, relative path preservation, resumability, failure continuation, output paths and overwrite protection, rational frame rates, FFprobe JSON, FFmpeg arguments, normalization publication behavior, broadcast rules, cancellation, keyframe intervals, catalog validation, matching ambiguity, metadata idempotence, dry-run safety, review, synchronization, rename/rebind, orphan reporting, and playlist eligibility. The integration test creates a tiny clip at runtime when FFmpeg and FFprobe are available and skips otherwise. No test media is committed.

## Further documentation

- [Raspberry Pi setup and deployment](docs/raspberry-pi-setup.md)
- [Broadcast standard](docs/broadcast-standard.md)
- [Media library and normalization workflow](docs/media-library.md)
- [Content catalog and asset metadata](docs/content-catalog.md)
