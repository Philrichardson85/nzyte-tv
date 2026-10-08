# NZYTE TV workstation setup

This focused guide covers installing tools, obtaining NZYTE TV source, publishing the command-line application, and preparing a portable media drive. For the complete operating sequence through live broadcast and tmux, start with the [operations runbook](operations-runbook.md).

For Raspberry Pi mounting and deployment details, see [Raspberry Pi setup](raspberry-pi-setup.md). For the complete media layout and ingest rules, see [Media library](media-library.md).

## What is supported and what has been verified

NZYTE TV targets:

- Windows x64
- Linux x64
- Linux ARM64
- Raspberry Pi 4 running Ubuntu 24.04 ARM64

NZYTE TV has been successfully:

- published as a self-contained Windows x64 application;
- published as a self-contained Linux ARM64 application;
- run on a Raspberry Pi 4;
- used to initialize a portable media root;
- used for H.264/AAC broadcast verification;
- used for one-pass portrait blurred-background normalization; and
- used for playlist generation.

Linux x64 is a supported target, but the list above does not claim a production acceptance test that has not been performed.

## Before entering commands

Commands in this guide are labeled **PowerShell** or **Bash**. PowerShell is the normal shell on Windows. Bash is common on Ubuntu and other Linux systems. Do not paste a PowerShell command into Bash or a Bash command into PowerShell.

Long commands use different continuation characters:

- PowerShell uses a backtick: `` ` ``
- Bash uses a backslash: `\`

The continuation character must be the last character on its line. If copying a multiline command is troublesome, copy the entire labeled block exactly.

## Choose a release checkout or a development checkout

A **tagged release** is a frozen, named version of the source. Use a tagged release for a production machine or a repeatable media-conversion workstation. In commands below, replace `vX.Y.Z` with an actual available tag.

The `main` branch is the latest development and integration state. Use it only when you intentionally want newer, potentially unreleased work.

The tag name is authoritative. Do not assume a version mentioned in an older guide is permanently the latest release.

## Windows x64 setup

### 1. Understand and check the prerequisites

NZYTE TV uses four command-line tools:

- **Git** downloads the source and switches between releases.
- **.NET 10 SDK** builds and publishes NZYTE TV.
- **FFmpeg** normalizes media into the broadcast format.
- **FFprobe** inspects source and normalized media. It is normally included with FFmpeg.

Open PowerShell and check each tool.

PowerShell:

```powershell
git --version
dotnet --version
ffmpeg -version
ffprobe -version
```

The `dotnet --version` result should begin with `10.`. Each media command should print version information rather than a command-not-found error.

Visual Studio is not required merely to build or run the NZYTE TV CLI. The .NET 10 SDK is sufficient.

### 2. Install missing prerequisites with winget

Run only the installation commands for tools that are missing.

PowerShell:

```powershell
winget install Git.Git
winget install Microsoft.DotNet.SDK.10
winget install Gyan.FFmpeg
```

After installation, **close every PowerShell window and open a new one**. Installers update `PATH`, but an already-open shell normally cannot see that change. Run the four version checks again.

If a package identifier changes or winget cannot find .NET 10, use Microsoft's [Windows .NET installation instructions](https://learn.microsoft.com/dotnet/core/install/windows) rather than substituting an older SDK.

### 3. Clone the repository and select the stable release

The following commands create `C:\_Code`, clone NZYTE TV, download release tags, and select a chosen release.

PowerShell:

```powershell
New-Item -ItemType Directory -Force C:\_Code | Out-Null
Set-Location C:\_Code

git clone https://github.com/Philrichardson85/nzyte-tv.git
Set-Location C:\_Code\nzyte-tv

git fetch --tags
git switch --detach vX.Y.Z
```

`--detach` is expected for a release checkout: it prevents the local checkout from silently moving when a branch changes.

Verify the selection.

PowerShell:

```powershell
git describe --tags --exact-match
git rev-parse --short HEAD
git status --short
```

The first command should report the chosen tag. An empty result from `git status --short` means there are no local file changes.

### 4. Build and test the checkout

This step confirms that the SDK can restore, compile, and test the source before it is published.

PowerShell:

```powershell
dotnet restore NzyteTv.slnx
dotnet build NzyteTv.slnx -c Release
dotnet test NzyteTv.slnx -c Release --no-build
```

The first restore may download .NET packages and therefore requires internet access.

### 5. Publish a self-contained Windows executable

Create a versioned destination and publish for the `win-x64` runtime.

PowerShell:

```powershell
New-Item -ItemType Directory -Force C:\Tools\NzyteTv\candidate | Out-Null

dotnet publish `
  src\NzyteTv.Cli\NzyteTv.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o C:\Tools\NzyteTv\candidate
```

Verify the published application.

PowerShell:

```powershell
C:\Tools\NzyteTv\candidate\nzytetv.exe --help
```

**Self-contained** means the published application includes the .NET runtime needed to **run** NZYTE TV. The computer that performs the build and publish still needs the .NET 10 SDK. FFmpeg and FFprobe remain external dependencies and must still be installed and discoverable through `PATH` when media commands run.

## Linux x64 and Linux ARM64 setup

These instructions use Ubuntu/Debian-style package commands. Other Linux distributions do not necessarily use `apt`; use the distribution's supported package manager and Microsoft's [.NET installation instructions for Linux](https://learn.microsoft.com/dotnet/core/install/linux).

### 1. Check the machine architecture

The publish runtime identifier must match the computer that will run the application.

Bash:

```bash
uname -m
```

Common results are:

- `x86_64`: use `linux-x64`
- `aarch64` or `arm64`: use `linux-arm64`

### 2. Check and install prerequisites

Bash:

```bash
git --version
dotnet --version
ffmpeg -version
ffprobe -version
```

On a supported Ubuntu/Debian setup, install Git and FFmpeg from the operating system packages.

Bash:

```bash
sudo apt update
sudo apt install -y git ffmpeg
```

Where the configured repositories provide .NET 10, install the SDK with:

Bash:

```bash
sudo apt install -y dotnet-sdk-10.0
```

.NET package availability varies by Linux distribution and release. If `apt` cannot find `dotnet-sdk-10.0`, do not install an older SDK and assume it is compatible. Follow Microsoft's [Ubuntu-specific .NET instructions](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu-install) for the installed Ubuntu version, then confirm that `dotnet --version` begins with `10.`.

The Ubuntu FFmpeg package includes FFprobe. Both commands must work before using media inspection or normalization.

### 3. Clone the repository and select the stable release

Create an application area owned by the current user, then clone the source into its `src` directory.

Bash:

```bash
sudo mkdir -p /opt/nzyte-tv
sudo chown "$USER":"$USER" /opt/nzyte-tv

cd /opt/nzyte-tv
git clone https://github.com/Philrichardson85/nzyte-tv.git src

cd /opt/nzyte-tv/src
git fetch --tags
git switch --detach vX.Y.Z
```

Verify the release checkout.

Bash:

```bash
git describe --tags --exact-match
git rev-parse --short HEAD
git status --short
```

The first command should report the chosen tag. No output from `git status --short` means the checkout is clean.

### 4. Build and test the checkout

Bash:

```bash
dotnet restore NzyteTv.slnx
dotnet build NzyteTv.slnx -c Release
dotnet test NzyteTv.slnx -c Release --no-build
```

### 5. Publish for Linux x64

Use this command only for an x64 Linux target.

Bash:

```bash
dotnet publish \
  src/NzyteTv.Cli/NzyteTv.Cli.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o /opt/nzyte-tv/app
```

Verify it.

Bash:

```bash
/opt/nzyte-tv/app/nzytetv --help
```

### 6. Publish for Linux ARM64 or Raspberry Pi

Use this command only for an ARM64 Linux target.

Bash:

```bash
dotnet publish \
  src/NzyteTv.Cli/NzyteTv.Cli.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o /opt/nzyte-tv/app
```

Verify it.

Bash:

```bash
/opt/nzyte-tv/app/nzytetv --help
```

Publishing for the wrong architecture can produce an application that cannot start on the target machine. See [Raspberry Pi setup](raspberry-pi-setup.md) for Pi-specific mounting, candidate deployment, and rollback guidance.

### Publish the separate read-only dashboard

Checkpoint 3B3-A uses a separate ASP.NET Core executable. Publish it to a different output directory from the broadcaster:

```bash
dotnet publish \
  src/NzyteTv.Dashboard/NzyteTv.Dashboard.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o artifacts/publish/dashboard-linux-arm64
```

The dashboard binds only to `127.0.0.1:5080` by default and does not replace or host inside `/opt/nzyte-tv/app/nzytetv`. This command creates workstation staging output; it does not install or start the candidate systemd unit. A later deployment or upgrade must use an operator-reviewed staged procedure rather than publishing over a live dashboard directory. See [Read-only web dashboard](dashboard.md) for the state boundary and SSH access.

## Updating NZYTE TV

Updating differs depending on whether the checkout tracks `main` or is pinned to a release tag.

### Update a development checkout that tracks main

On Windows:

PowerShell:

```powershell
Set-Location C:\_Code\nzyte-tv
git switch main
git fetch origin
git pull --ff-only origin main
```

On Linux:

Bash:

```bash
cd /opt/nzyte-tv/src
git switch main
git fetch origin
git pull --ff-only origin main
```

`--ff-only` refuses to create an accidental merge commit. If local work and the remote branch have diverged, Git stops and makes that situation visible instead of silently combining them.

### Update or restore a tag-pinned production checkout

Fetching tags does not automatically switch the installed release. On Windows:

PowerShell:

```powershell
Set-Location C:\_Code\nzyte-tv
git fetch --tags
git switch --detach vX.Y.Z
```

On Linux:

Bash:

```bash
cd /opt/nzyte-tv/src
git fetch --tags
git switch --detach vX.Y.Z
```

Replace `vX.Y.Z` with the chosen release tag. A workstation pinned to a tag does not receive a newer release automatically.

### Republish after changing source versions

Pulling or checking out source code does **not** update an executable that was published earlier. Build and publish again after changing the checkout.

Windows example:

PowerShell:

```powershell
dotnet publish `
  src\NzyteTv.Cli\NzyteTv.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o C:\Tools\NzyteTv\candidate
```

Linux ARM64 example:

Bash:

```bash
dotnet publish \
  src/NzyteTv.Cli/NzyteTv.Cli.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o /opt/nzyte-tv/app
```

Use `linux-x64` instead when the destination machine is Linux x64. Publishing directly over a directory currently serving production can leave a running deployment difficult to roll back. The [Raspberry Pi setup guide](raspberry-pi-setup.md#candidate-and-rollback-deployment) describes a safer candidate and rollback workflow.

## Prepare and use a portable media drive

The NZYTE TV repository and executable do not need to live on the media drive. The drive carries original media, normalized media, catalog data, metadata sidecars, technical manifests, and optionally playlists/history between computers.

### 1. Initialize the media root

First identify the correct drive letter or mount point. The command creates directories and small descriptor/catalog files; it does **not** format or partition the drive.

Windows example:

PowerShell:

```powershell
C:\Tools\NzyteTv\candidate\nzytetv.exe media init D:\
```

Linux example:

Bash:

```bash
/opt/nzyte-tv/app/nzytetv media init /mnt/nzyte-media
```

The drive letter or mount point may change between computers. These two paths can describe the same portable content:

- Windows: `D:\source\Music Videos\...`
- Linux or Raspberry Pi: `/srv/nzyte-tv/media/source/Music Videos/...`

The absolute host path is not part of an asset's identity.

Resulting layout (reference, not a command):

```text
<media-root>/
    .nzytetv-media-root.json
    source/
        Music Videos/
        Lyric Videos/
        Performance Videos/
        Visualizers/
        Animated Visuals/
        Vlog Episodes/
        Bumpers/
        Promos/
        Interstitials/
        Advertisements/
        Specials/
    library/
    catalog/
        song-catalog.json
        programming.json        optional; created explicitly
    playlists/
    work/
```

The directories have distinct purposes:

- `source` contains original masters. Copy each master into its correct category directory.
- `source/Visualizers` contains full-song static or lightly animated graphical presentations.
- `source/Animated Visuals` contains animated, narrative, cinematic, anime/movie-style, AI-animated, or similar extended song presentations.
- `library` is managed by NZYTE TV and contains normalized, verified broadcast media. Do not manually put normalized files there unless project documentation explicitly instructs you to do so.
- `catalog/song-catalog.json` contains canonical song identity and grouping.
- optional `catalog/programming.json` contains sparse Checkpoint 3A editorial and sequencing policy; `media init` does not create it.
- Programming metadata sidecars and technical normalization manifests travel with their related library assets.
- `playlists` can hold generated playlists and history.
- `work` is available for media-root working data.

`media init` is safe and idempotent: running it again adds missing structure without deleting media or overwriting a valid existing catalog. After upgrading the NZYTE TV executable, rerunning it against an existing portable root adds any missing category directories while preserving the descriptor, source/library media, catalog, manifests, metadata sidecars, playlists, and history. It never formats the drive. Do not alter or attempt to ingest Windows filesystem directories such as `System Volume Information`.

Both new categories are song-based. Their files receive distinct `assetId` values, but every presentation of the same recording must resolve to the same catalog `contentGroupId`. A folder name supplies the programming type only; unresolved or ambiguous song filenames still require metadata review.

### 2. Normalize source media on the workstation

Copy visualizer masters into `D:\source\Visualizers\` and animated/narrative song masters into `D:\source\Animated Visuals\`. The same instructions apply after `media init` upgrades an existing drive by adding those missing directories.

This Windows example applies the explicit portrait treatment while normalizing the entire source tree.

PowerShell:

```powershell
C:\Tools\NzyteTv\candidate\nzytetv.exe normalize-library `
  "D:\source" `
  "D:\library" `
  --vertical-layout blurred-background
```

Equivalent Linux example:

Bash:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /mnt/nzyte-media/source \
  /mnt/nzyte-media/library \
  --vertical-layout blurred-background
```

During this operation:

- landscape media follows the normal broadcast-normalization path;
- portrait media receives the blurred 16:9 background treatment;
- portrait presentation and broadcast normalization happen in one encode;
- original files remain under `source`;
- normalized outputs are published under the matching relative path in `library`;
- technical manifests support verification and source-change detection; and
- later runs skip outputs only when the destination is verified and its manifest is current.

FFmpeg and FFprobe must still be installed on the normalization workstation. A Raspberry Pi does not need to re-encode media merely because the drive letter changed into a Linux mount path.

### 3. Initialize programming metadata and move the drive

After normalization, initialize programming metadata against the catalog that travels on the drive. Review any assets the conservative matcher cannot resolve automatically.

PowerShell:

```powershell
C:\Tools\NzyteTv\candidate\nzytetv.exe metadata initialize `
  "D:\source" `
  "D:\library" `
  --catalog "D:\catalog\song-catalog.json"

C:\Tools\NzyteTv\candidate\nzytetv.exe metadata review `
  "D:\source" `
  "D:\library" `
  --catalog "D:\catalog\song-catalog.json"

C:\Tools\NzyteTv\candidate\nzytetv.exe metadata sync "D:\source" "D:\library"

C:\Tools\NzyteTv\candidate\nzytetv.exe programming init --media-root "D:\"
C:\Tools\NzyteTv\candidate\nzytetv.exe programming validate --media-root "D:\"

C:\Tools\NzyteTv\candidate\nzytetv.exe build-playlist `
  "D:\library" `
  --catalog "D:\catalog\song-catalog.json" `
  --output "D:\playlists\current.json" `
  --history "D:\playlists\history.json" `
  --duration 6h
```

Bash:

```bash
/opt/nzyte-tv/app/nzytetv metadata initialize \
  /mnt/nzyte-media/source \
  /mnt/nzyte-media/library \
  --catalog /mnt/nzyte-media/catalog/song-catalog.json

/opt/nzyte-tv/app/nzytetv metadata review \
  /mnt/nzyte-media/source \
  /mnt/nzyte-media/library \
  --catalog /mnt/nzyte-media/catalog/song-catalog.json

/opt/nzyte-tv/app/nzytetv metadata sync \
  /mnt/nzyte-media/source \
  /mnt/nzyte-media/library

/opt/nzyte-tv/app/nzytetv programming init --media-root /mnt/nzyte-media
/opt/nzyte-tv/app/nzytetv programming validate --media-root /mnt/nzyte-media

/opt/nzyte-tv/app/nzytetv build-playlist \
  /mnt/nzyte-media/library \
  --catalog /mnt/nzyte-media/catalog/song-catalog.json \
  --output /mnt/nzyte-media/playlists/current.json \
  --history /mnt/nzyte-media/playlists/history.json \
  --duration 6h
```

The two `programming` commands explicitly activate optional Checkpoint 3A policy; omit them to retain legacy scheduler behavior. Initialization never overwrites an existing policy. These metadata and programming commands do not invoke FFmpeg or re-encode media. When normalization and metadata review are complete, use the operating system's safe-eject function before unplugging the drive. Mount the same drive on the Raspberry Pi and use its normalized `library/`; an operating-system or mount-path change alone does not require normalization again.

See [Media library](media-library.md) for the full workstation-to-Pi workflow, [Content catalog](content-catalog.md) for catalog and review details, and [V1 programming policy](programming.md) for editorial controls and scheduling behavior.

## Troubleshooting

### 1. `git` is not recognized or `git: command not found`

Install Git, close and reopen the shell, and run `git --version`. On Windows, inspect command discovery with:

PowerShell:

```powershell
Get-Command git
```

On Linux:

Bash:

```bash
command -v git
```

### 2. `dotnet` is not recognized or `dotnet: command not found`

Confirm that the **.NET 10 SDK**, not merely an older runtime, is installed. Reopen PowerShell after a Windows installation, then run:

PowerShell:

```powershell
dotnet --version
Get-Command dotnet
```

On Linux:

Bash:

```bash
dotnet --version
command -v dotnet
```

If the command remains missing, follow Microsoft's installation instructions for the exact operating system version.

### 3. `ffmpeg` is not recognized

Reopen the shell after installation and verify command discovery.

PowerShell:

```powershell
ffmpeg -version
Get-Command ffmpeg
```

Bash:

```bash
ffmpeg -version
command -v ffmpeg
```

### 4. `ffprobe` is not recognized

FFprobe normally ships with FFmpeg. If FFmpeg works but FFprobe does not, verify that the installed FFmpeg package includes both executables and that both are on `PATH`.

PowerShell:

```powershell
ffprobe -version
Get-Command ffprobe
```

Bash:

```bash
ffprobe -version
command -v ffprobe
```

### 5. PowerShell still cannot see a newly installed tool

Close **all** open PowerShell and terminal windows, open a new PowerShell window, and retry the version command. If it still fails, use `Get-Command` as shown above, confirm the install completed successfully, and repair or reinstall the package. Avoid copying executables into random directories as a PATH workaround.

### 6. The USB drive letter is wrong

Do not guess, and do not format a drive to fix a path error. On Windows, inspect mounted volumes before running `media init`.

PowerShell:

```powershell
Get-Volume
```

On Linux, inspect block devices and their current mount points.

Bash:

```bash
lsblk -f
```

Substitute the actual drive letter or mounted directory in later commands.

### 7. A Linux multiline command was pasted into PowerShell

PowerShell does not use Bash's trailing backslash for continuation. Use the PowerShell block from this guide, with a trailing backtick on each continued line.

### 8. A PowerShell backtick command was pasted into Bash

Bash does not use PowerShell's backtick continuation syntax. Use the Bash block from this guide, with a trailing backslash on each continued line.

### 9. `git pull --ff-only` refuses because the repository has local changes or has diverged

Do not reset or discard files merely to make the pull succeed. Inspect the state first.

PowerShell:

```powershell
git status --short
git branch --show-current
```

Bash:

```bash
git status --short
git branch --show-current
```

Local changes may be valuable work. Commit them on an appropriate development branch, preserve them using a workflow you understand, or ask an experienced Git user for help before updating. A tag-pinned production checkout should normally be clean.

### 10. The executable did not change after pulling newer source

Git updates source files only. Run the appropriate `dotnet publish` command again, then invoke `--help` from the exact output path you republished. Check that an old copy earlier on `PATH` is not the executable being run.

### 11. The workstation is on `main` when a reproducible release was intended

Fetch tags, switch to the desired tag, and verify it.

PowerShell:

```powershell
git fetch --tags
git switch --detach vX.Y.Z
git describe --tags --exact-match
```

Bash:

```bash
git fetch --tags
git switch --detach vX.Y.Z
git describe --tags --exact-match
```

### 12. The published application reports an architecture or executable-format error

Publish with the runtime identifier that matches the destination machine:

- `win-x64` for 64-bit Windows
- `linux-x64` for x64 Linux (`uname -m` normally reports `x86_64`)
- `linux-arm64` for ARM64 Linux or Raspberry Pi (`uname -m` normally reports `aarch64` or `arm64`)

Do not copy a Windows executable to Linux or an x64 Linux build to an ARM64 Pi and expect it to run. Republish from the source using the correct `-r` value.
