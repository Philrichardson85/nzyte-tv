# NZYTE TV

NZYTE TV v0.1 is the media-preparation foundation for a future 24/7 prerecorded YouTube broadcast system. It inspects source media, normalizes it to one deterministic broadcast format, and independently verifies the resulting file—including its actual keyframe timestamps. Streaming, scheduling, playlists, YouTube integration, and services are intentionally out of scope for this release.

The primary production target is a Raspberry Pi 4 Model B with 4 GB RAM running Ubuntu 22.04. Windows x64 and Linux ARM64 are first-class targets. The application targets .NET 10 and launches the external `ffmpeg` and `ffprobe` executables directly; those tools are not bundled.

## Commands

```text
nzytetv inspect <input>
nzytetv normalize <input> [--overwrite]
nzytetv verify <input>
```

Use `--help` on the root command or any subcommand. Exit code `0` means success. Invalid arguments and operational or verification failures return non-zero codes.

`normalize` writes `./BroadcastReady/<original-file-name>.mp4`, relative to the current working directory. It never modifies the source. An existing destination is rejected unless `--overwrite` is supplied, and even that option can never overwrite the source itself. Encoding occurs in a temporary file; the final destination appears only after verification succeeds. If a source has no audio, NZYTE TV generates silent 48 kHz stereo AAC audio.

## Windows development setup

These instructions start from a clean Windows x64 machine. Run the installation commands in PowerShell or install the same products using their official installers.

1. Install Git.

   ```powershell
   winget install --id Git.Git -e
   ```

2. Install the .NET 10 SDK.

   ```powershell
   winget install --id Microsoft.DotNet.SDK.10 -e
   ```

3. Install FFmpeg (the package includes FFprobe).

   ```powershell
   winget install --id Gyan.FFmpeg -e
   ```

   Close and reopen PowerShell so its `PATH` includes newly installed programs.

4. Verify Git.

   ```powershell
   git --version
   ```

5. Verify .NET 10.

   ```powershell
   dotnet --version
   ```

   The output must begin with `10.`.

6. Verify FFmpeg.

   ```powershell
   ffmpeg -version
   ```

7. Verify FFprobe.

   ```powershell
   ffprobe -version
   ```

8. Clone the repository and enter it. Replace `<repository-url>` with this repository's HTTPS or SSH clone URL.

   ```powershell
   git clone <repository-url> nzyte_tv
   Set-Location nzyte_tv
   ```

9. Restore packages.

   ```powershell
   dotnet restore NzyteTv.slnx
   ```

10. Build the complete solution.

    ```powershell
    dotnet build NzyteTv.slnx --no-restore
    ```

11. Run all tests.

    ```powershell
    dotnet test NzyteTv.slnx --no-build --no-restore
    ```

12. Inspect a source file.

    ```powershell
    dotnet run --project src/NzyteTv.Cli -- inspect "C:\Media\video.mp4"
    ```

13. Normalize it. Run this command from the directory where `BroadcastReady` should be created.

    ```powershell
    dotnet run --project src/NzyteTv.Cli -- normalize "C:\Media\video.mp4"
    ```

    To intentionally replace an existing normalized destination:

    ```powershell
    dotnet run --project src/NzyteTv.Cli -- normalize "C:\Media\video.mp4" --overwrite
    ```

14. Verify a prepared file independently.

    ```powershell
    dotnet run --project src/NzyteTv.Cli -- verify ".\BroadcastReady\video.mp4"
    ```

## Raspberry Pi / Ubuntu setup

The current production hardware target is a Raspberry Pi 4 Model B with 4 GB RAM running 64-bit Ubuntu 22.04. Confirm that the installed OS is ARM64 with `dpkg --print-architecture`; it should print `arm64`.

Install the runtime media dependencies:

1. Refresh package indexes.

   ```bash
   sudo apt update
   ```

2. Install Git.

   ```bash
   sudo apt install -y git
   ```

3. Install FFmpeg and FFprobe.

   ```bash
   sudo apt install -y ffmpeg
   ```

4. Verify FFmpeg.

   ```bash
   ffmpeg -version
   ```

5. Verify FFprobe.

   ```bash
   ffprobe -version
   ```

For development builds on Ubuntu 22.04, add Microsoft's package repository and install the .NET 10 SDK:

```bash
sudo apt install -y wget
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-10.0
dotnet --version
```

Then clone, build, test, and run:

```bash
git clone <repository-url> nzyte_tv
cd nzyte_tv
dotnet restore NzyteTv.slnx
dotnet build NzyteTv.slnx --no-restore
dotnet test NzyteTv.slnx --no-build --no-restore
dotnet run --project src/NzyteTv.Cli -- inspect "/home/ubuntu/media/video.mp4"
dotnet run --project src/NzyteTv.Cli -- normalize "/home/ubuntu/media/video.mp4"
dotnet run --project src/NzyteTv.Cli -- verify "./BroadcastReady/video.mp4"
```

Future GitHub Releases should publish self-contained `linux-arm64` binaries. Once those release artifacts are available, the Raspberry Pi will not require the .NET SDK or runtime; FFmpeg and FFprobe will remain required external packages.

## Publishing self-contained builds

The release workflow publishes single-file, self-contained application builds for `win-x64`, `linux-x64`, and `linux-arm64`. Equivalent local commands are:

```powershell
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-arm64
```

Self-contained means the .NET runtime is included. FFmpeg and FFprobe are still external dependencies and must be on `PATH`.

## Architecture and testing

- `NzyteTv.Cli` owns argument handling and console presentation.
- `NzyteTv.Core` owns media-domain models, output safety, rational-number handling, and broadcast validation. It has no FFmpeg dependency.
- `NzyteTv.Media` owns executable discovery, asynchronous process execution, typed FFprobe JSON parsing, normalization, and verification orchestration.

Unit tests cover command parsing, output paths and overwrite safety, rational frame rates, FFprobe JSON, encoding arguments, broadcast rules, and keyframe intervals. The integration test creates a tiny clip at runtime and runs only when both FFmpeg and FFprobe are installed; otherwise it is reported as skipped. No media files are stored in the repository.

See [docs/broadcast-standard.md](docs/broadcast-standard.md) for the admission standard and its operational background.
