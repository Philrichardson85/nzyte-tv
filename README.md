# NZYTE TV

NZYTE TV v0.1 is the media-preparation foundation for a future 24/7 prerecorded YouTube broadcast system. It inspects source media, normalizes it to one deterministic broadcast format, and independently verifies the result, including actual keyframe timestamps.

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
- SanDisk Cruzer Glide NTFS media drive mounted at `/srv/nzyte-tv/media`.

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

## CLI

```text
nzytetv inspect <input>
nzytetv normalize <input> [--overwrite]
nzytetv normalize-library <source-root> <destination-root> [--overwrite]
nzytetv verify <input>
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

Without `--overwrite`, an existing destination is independently verified. A valid destination is recorded as a verified skip; an invalid or unreadable destination is recorded as a failure and is not silently accepted. With `--overwrite`, only the destination is replaced, and only after the new temporary output passes verification.

One failed file does not stop later files. The final summary reports discovered, normalized, verified-skipped, failed, and verified-ready counts. Any per-file failure makes the command exit nonzero. Ctrl+C cancels the active FFmpeg process and does not publish its partial output.

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

## Recommended deployment layout

```text
/opt/nzyte-tv/
|-- src/       repository checkout
`-- app/       published production application

/srv/nzyte-tv/
|-- media/     USB media mount
|-- incoming/  staging for newly received media
|-- work/      normalization working directory
`-- logs/      future operational logs
```

The verified executable is `/opt/nzyte-tv/app/nzytetv`. It is not named `NzyteTv.Cli`.

## Raspberry Pi quick-start deployment

This quick start assumes Ubuntu Desktop 24.04.5 ARM64 is already installed, SSH is configured, FFmpeg is installed, .NET 10 is installed, and the USB media drive is mounted. Use the [full Raspberry Pi setup guide](docs/raspberry-pi-setup.md) when starting from a blank Pi.

Create the directory layout:

```bash
sudo mkdir -p /opt/nzyte-tv/src /opt/nzyte-tv/app
sudo mkdir -p /srv/nzyte-tv/incoming /srv/nzyte-tv/work /srv/nzyte-tv/logs
sudo chown -R "$USER":"$USER" /opt/nzyte-tv
sudo chown -R "$USER":"$USER" /srv/nzyte-tv/incoming /srv/nzyte-tv/work /srv/nzyte-tv/logs
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
  "/srv/nzyte-tv/media/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4"
```

Run normalization from the desired working directory:

```bash
cd /srv/nzyte-tv/work
/opt/nzyte-tv/app/nzytetv normalize \
  "/srv/nzyte-tv/media/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4"
/opt/nzyte-tv/app/nzytetv verify \
  "/srv/nzyte-tv/work/BroadcastReady/Lady Lady - Nzyte (Official Music Video).mp4"
```

The verified normalization and automatic verification took 13 minutes 38 seconds on the Raspberry Pi 4. The independent verification reported `RESULT: BROADCAST READY`.

For a complete library run, the production command is:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /srv/nzyte-tv/media \
  /srv/nzyte-tv/work/BroadcastReady
```

This batch command is implemented and covered by automated tests. A complete 39-file run on the Raspberry Pi has not yet been recorded, so no total runtime is claimed. It is safe to stop and rerun: verified destinations are skipped unless `--overwrite` is supplied.

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
- `NzyteTv.Core` owns domain models, output safety, rational-number handling, and broadcast validation. It has no FFmpeg dependency.
- `NzyteTv.Media` owns tool discovery, asynchronous process execution, typed FFprobe JSON parsing, normalization, and verification orchestration.

Tests cover command parsing, recursive library discovery, extension filtering, relative path preservation, resumability, failure continuation, output paths and overwrite protection, rational frame rates, FFprobe JSON, FFmpeg arguments, normalization publication behavior, broadcast rules, cancellation, and keyframe intervals. The integration test creates a tiny clip at runtime when FFmpeg and FFprobe are available and skips otherwise. No test media is committed.

## Further documentation

- [Raspberry Pi setup and deployment](docs/raspberry-pi-setup.md)
- [Broadcast standard](docs/broadcast-standard.md)
- [Media library and normalization workflow](docs/media-library.md)
