# Raspberry Pi 4 deployment guide

This guide reproduces the tested NZYTE TV deployment from a blank Raspberry Pi 4. It covers the operating system, remote administration, media storage, .NET, FFmpeg, application publishing, and the first real normalization run.

## Status labels

- **Verified** means the step or behavior was observed on the deployed Raspberry Pi.
- **Recommended** means it is the procedure to use for repeatable setup, but every variation has not been tested.
- **Not tested** means it may be useful later but must not be treated as established behavior.

## Verified deployment

| Component | Verified value |
|---|---|
| Hardware | Raspberry Pi 4 Model B, 4 GB RAM |
| OS/application storage | `/dev/mmcblk0p2`, ext4, approximately 29 GB usable |
| Media storage | SanDisk Cruzer Glide `/dev/sda1`, NTFS3, approximately 30 GB |
| Display connection | Raspberry Pi 4 micro-HDMI |
| OS | Ubuntu Desktop 24.04.5 LTS, 64-bit |
| Architecture | ARM64 / `aarch64` |
| Graphical session | Xorg/X11 (`Type=x11`) |
| FFmpeg / FFprobe | `6.1.1-3ubuntu5` |
| .NET SDK | `10.0.112` |
| .NET host/runtime | `10.0.12` |
| .NET RID | `ubuntu.24.04-arm64` |
| Application executable | `/opt/nzyte-tv/app/nzytetv` |
| Media mount | `/srv/nzyte-tv/media` |

## 1. Prepare the microSD card

### Flash Ubuntu

1. On a Windows computer, download Raspberry Pi Imager from <https://www.raspberrypi.com/software/>.
2. Insert the 32 GB microSD card.
3. Open Raspberry Pi Imager.
4. Select **Raspberry Pi 4** as the device.
5. Select **Ubuntu Desktop 24.04.5 LTS (64-bit)** as the operating system.
6. Select the microSD card as the storage target.
7. Write and verify the image.

These device and OS selections are the verified choices. Do not substitute Ubuntu Server if the machine needs the tested local desktop and AnyDesk workflow.

### Recover from the tested Windows write error

The first imaging attempt failed with:

```text
Access denied error while writing file to disk
```

DiskPart reported both read-only fields as `No`:

```text
Current Read-only State : No
Read-only               : No
```

The successful fix was to remove the SD card's partition table with DiskPart and retry Raspberry Pi Imager.

> **Destructive warning:** `clean` destroys the partition table on the selected disk. Selecting the wrong disk can make a Windows system disk or another data drive unusable. Verify the disk number, capacity, and removable-media identity before running it. This procedure is only for the SD card being reimaged.

Open an elevated Command Prompt or PowerShell and run:

```text
diskpart
list disk
select disk <SD-CARD-DISK-NUMBER>
detail disk
attributes disk
clean
exit
```

Check `detail disk` one final time before `clean`. After the verified SD card was cleaned, Raspberry Pi Imager wrote the Ubuntu image successfully.

## 2. First boot

1. Insert the microSD card in the Raspberry Pi.
2. Connect a keyboard, mouse, network connection, and physical monitor. Raspberry Pi 4 uses a micro-HDMI connector.
3. Power on the Pi.
4. Complete the Ubuntu first-run wizard, including the user account, password, locale, time zone, and network.
5. Log in to the graphical desktop.
6. Open Terminal and confirm the environment:

   ```bash
   lsb_release -a
   uname -m
   dpkg --print-architecture
   ```

   The verified architecture outputs are `aarch64` from `uname -m` and `arm64` from `dpkg --print-architecture`.

7. **Recommended:** update installed Ubuntu packages, then reboot if requested:

   ```bash
   sudo apt update
   sudo apt upgrade -y
   sudo reboot
   ```

## 3. Enable SSH

Install and start the OpenSSH server:

```bash
sudo apt update
sudo apt install openssh-server -y
sudo systemctl enable --now ssh
```

Verify the service and find the Pi's network address:

```bash
systemctl status ssh
hostname -I
```

From another computer, connect with the Ubuntu user name and one of the reported IP addresses:

```bash
ssh u24@<PI-IP-ADDRESS>
```

Replace `u24` if a different Ubuntu account was created.

## 4. Install and configure AnyDesk

### Install the Raspberry Pi package

The verified installation used AnyDesk's Raspberry Pi-specific ARM64 `.deb`, not a generic package.

1. On the Pi, open <https://anydesk.com/en/downloads/raspberry-pi>.
2. Download the Raspberry Pi `.deb` package.
3. Open the downloaded `.deb` with Ubuntu App Center.
4. Click the green **Install** button.
5. Confirm the installation and enter the Ubuntu password when prompted.
6. Open **Show Apps**, launch AnyDesk, and close its first-run welcome screen.
7. Record the connection ID displayed on the main AnyDesk screen.

### Use Xorg for incoming sessions

Incoming AnyDesk sessions failed under Wayland with:

```text
Session Closed
display_server_not_supported
```

The tested safe fix changes only the login session:

1. Log out of Ubuntu.
2. At the login screen, select the user.
3. Click the gear icon.
4. Select **Ubuntu on Xorg**.
5. Log in again.

Do not use a global `WaylandEnable=false` setting as the primary setup path. It was not needed for the verified deployment and changes the display manager for every session.

This matches [AnyDesk's Linux/Raspberry Pi guidance](https://support.anydesk.com/anydesk-for-linux-raspberry-pi): incoming Linux sessions require a graphical Xorg session, while Wayland is not supported for incoming sessions.

From a terminal inside the graphical desktop, this should report `x11`:

```bash
echo "$XDG_SESSION_TYPE"
```

An SSH shell can report `tty`; that describes the SSH shell, not the graphical desktop. From SSH, identify and inspect the graphical session instead:

```bash
loginctl list-sessions
loginctl show-session <SESSION-ID> -p Type -p Name -p Remote -p State
```

The verified graphical session included:

```text
Type=x11
```

### Configure unattended access

The verified AnyDesk UI procedure was:

1. Open **Settings > Security**.
2. Click **Unlock**.
3. Enter the Ubuntu password.
4. Enable unattended access.
5. Allow other devices to save login tokens for this client.
6. Set a strong, unique AnyDesk unattended-access password.
7. Select the **Unattended Access** permission profile.
8. From the Windows AnyDesk client, connect to the Pi's ID and authenticate with that password.
9. Confirm that a later unattended reconnect succeeds.

Never place the AnyDesk password in this repository, a shell script, or a command history entry.

### Physical monitor requirement

In this tested setup, a connected and active physical monitor is required for AnyDesk to display the desktop:

- AnyDesk showed a blank remote display when no active monitor was connected.
- Xorg was still running on display `:0`.
- `/tmp/.X11-unix/X0` existed.
- `xrandr` reported a 1024x768 screen while `HDMI-1` and `HDMI-2` were disconnected.
- Connecting a physical monitor immediately made the desktop appear in AnyDesk.
- Turning that monitor off caused the AnyDesk display to stop working again.

Use a physical monitor connected through the Pi's micro-HDMI port for this deployment. A dummy HDMI adapter is a possible future workaround, but it has **not been tested** and must not be documented as a confirmed solution.

## 5. Install and verify FFmpeg

Install the Ubuntu package:

```bash
sudo apt update
sudo apt install ffmpeg -y
```

Verify both required executables and the machine architecture:

```bash
ffmpeg -version
ffprobe -version
uname -m
```

The deployed Pi reported:

```text
ffmpeg 6.1.1-3ubuntu5
ffprobe 6.1.1-3ubuntu5
aarch64
```

The build reported support for `vdpau`, `cuda`, `vaapi`, `drm`, `opencl`, and `vulkan`. Relevant listed encoders included:

```text
libx264
libx265
h264_v4l2m2m
hevc_v4l2m2m
h264_vaapi
hevc_vaapi
```

The Ubuntu build includes `--disable-omx`; do not design or document an OMX dependency.

### Hardware encoder status

Hardware H.264 encoding with `h264_v4l2m2m` was confirmed to start successfully. FFmpeg selected:

```text
/dev/video11
driver: bcm2835-codec
card: bcm2835-codec-encode
```

A synthetic 1280x720 30 fps test ran at approximately 5.06x real time. However, that test emitted `Non-monotonic DTS`, and later FFprobe output reported a missing keyframe or missing picture at the beginning.

Therefore:

- hardware encoder availability is **verified**;
- that exact hardware output path is **not validated** for NZYTE TV broadcast-ready media;
- the current normalizer's software `libx264` path remains the verified application workflow;
- an encoder appearing in `ffmpeg -encoders` does not prove that it works correctly on this hardware or meets the broadcast standard.

## 6. Install .NET 10

Ubuntu 24.04 provides .NET packages through Ubuntu's package feeds. Install the SDK used to build and publish NZYTE TV, following [Microsoft's Ubuntu installation guidance](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install):

```bash
sudo apt update
sudo apt install dotnet-sdk-10.0 -y
```

Verify it:

```bash
dotnet --version
dotnet --list-sdks
dotnet --info
```

The verified deployment reported:

```text
SDK: 10.0.112
Host/runtime: 10.0.12
OS: Ubuntu 24.04
RID: ubuntu.24.04-arm64
dotnet --list-sdks: 10.0.112
```

Package updates may install a later .NET 10 patch. Keep the major version at 10 unless the repository target framework changes.

## 7. Mount the USB media drive

### Verified drive

| Property | Value |
|---|---|
| Model | SanDisk Cruzer Glide |
| Device | `/dev/sda` |
| Partition | `/dev/sda1` |
| Filesystem | NTFS3 |
| Approximate size | 30 GB |
| UUID | `F424A3DE24A3A25A` |
| Permanent mount | `/srv/nzyte-tv/media` |
| Verified owner | `u24 u24` |

The drive already contains media. **Do not format it.** Device names such as `/dev/sda1` can change; the persistent configuration uses the filesystem UUID instead.

Inspect the device before changing mount configuration:

```bash
lsblk -o NAME,SIZE,FSTYPE,LABEL,UUID,MOUNTPOINTS
sudo blkid /dev/sda1
id u24
```

Confirm that the expected UUID is present and that the deployment account has UID and GID `1000`. If the account IDs differ, adjust the `uid` and `gid` values below rather than copying them blindly.

Create the mount point and back up `/etc/fstab`:

```bash
sudo mkdir -p /srv/nzyte-tv/media
sudo cp /etc/fstab /etc/fstab.pre-nzyte-tv
sudoedit /etc/fstab
```

Add the verified entry as one line:

```fstab
UUID=F424A3DE24A3A25A /srv/nzyte-tv/media ntfs3 defaults,uid=1000,gid=1000,umask=022,nofail,x-systemd.automount 0 0
```

Validate without rebooting:

```bash
sudo mount -a
ls -la /srv/nzyte-tv/media
findmnt /srv/nzyte-tv/media
ls -ld /srv/nzyte-tv/media
```

Reboot and test again:

```bash
sudo reboot
```

After reconnecting:

```bash
findmnt /srv/nzyte-tv/media
ls /srv/nzyte-tv/media
findmnt /srv/nzyte-tv/media
```

Because `x-systemd.automount` is enabled, the first `findmnt` after boot can show `systemd-1` and filesystem type `autofs`. Accessing the directory triggers the real NTFS mount; the second `findmnt` should then show the mounted drive. The verified ownership was `u24 u24`.

The OS partition is `/dev/mmcblk0p2`, uses ext4, and provides approximately 29 GB. Reserve that storage primarily for Ubuntu, the application, repository, logs, playlists, and working data. Keep large source and normalized media on the external drive.

See [media-library.md](media-library.md) for the directory inventory and handling rules.

## 8. Create the deployment layout

The deployed layout separates source, published application files, mutable working data, logs, playlists, and permanent USB media:

```text
/opt/nzyte-tv/
|-- src/
`-- app/

/srv/nzyte-tv/
|-- logs/
|-- playlists/
|-- work/
|-- media/                         external USB mount
|   |-- source/                    original/master media
|   |-- library/                   normalized and verified assets
|   `-- System Volume Information/ Windows/NTFS metadata; ignore
|-- source -> /srv/nzyte-tv/media/source
`-- library -> /srv/nzyte-tv/media/library
```

Create the directories and grant the deployment user ownership of application and mutable local directories:

```bash
sudo mkdir -p /opt/nzyte-tv/src /opt/nzyte-tv/app
sudo mkdir -p /srv/nzyte-tv/work /srv/nzyte-tv/logs /srv/nzyte-tv/playlists
sudo chown -R "$USER":"$USER" /opt/nzyte-tv
sudo chown -R "$USER":"$USER" /srv/nzyte-tv/work /srv/nzyte-tv/logs /srv/nzyte-tv/playlists

mkdir -p /srv/nzyte-tv/media/source /srv/nzyte-tv/media/library
sudo ln -s /srv/nzyte-tv/media/source /srv/nzyte-tv/source
sudo ln -s /srv/nzyte-tv/media/library /srv/nzyte-tv/library
```

The ownership of `/srv/nzyte-tv/media` comes from the NTFS mount options and should not be changed recursively. The `ln -s` commands are for a blank deployment; if either convenience path already exists, inspect it with `ls -ld` instead of replacing it blindly.

Maintenance commands do not always follow directory symlinks by default. Use `-L` when inspecting content through the convenience paths:

```bash
du -shL /srv/nzyte-tv/source
find -L /srv/nzyte-tv/source -type f
```

## 9. Clone, test, and publish NZYTE TV

Clone into the source directory:

```bash
git clone https://github.com/Philrichardson85/nzyte-tv.git /opt/nzyte-tv/src
cd /opt/nzyte-tv/src
```

Restore, build, and test the solution:

```bash
dotnet restore NzyteTv.slnx
dotnet build NzyteTv.slnx --configuration Release --no-restore
dotnet test NzyteTv.slnx --configuration Release --no-build --no-restore
```

Publish only the CLI project to the production application directory:

```bash
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o /opt/nzyte-tv/app
```

Do not publish the whole solution with one `-o` output directory. That produced `NETSDK1194` and mixed project and test output. The production executable is:

```text
/opt/nzyte-tv/app/nzytetv
```

It is not named `NzyteTv.Cli`.

## 10. Validate the CLI

Check executable help and each command's help without touching media:

```bash
/opt/nzyte-tv/app/nzytetv --help
/opt/nzyte-tv/app/nzytetv inspect --help
/opt/nzyte-tv/app/nzytetv normalize --help
/opt/nzyte-tv/app/nzytetv normalize-library --help
/opt/nzyte-tv/app/nzytetv verify --help
```

The original commands `inspect`, `normalize`, and `verify` were verified on the deployed Pi. `normalize-library` completed both the representative nine-file acceptance run and the full 39-file production run, including unchanged second-run verification tests.

Inspect a quoted source-master path through the convenience symlink:

```bash
/opt/nzyte-tv/app/nzytetv inspect \
  "/srv/nzyte-tv/source/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4"
```

## 11. Run the first normalization test

The output location is based on the current working directory. Change to `/srv/nzyte-tv/work` before normalizing so output is created under `/srv/nzyte-tv/work/BroadcastReady`:

```bash
cd /srv/nzyte-tv/work

/opt/nzyte-tv/app/nzytetv normalize \
  "/srv/nzyte-tv/source/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4"
```

The command creates and automatically verifies:

```text
/srv/nzyte-tv/work/BroadcastReady/Lady Lady - Nzyte (Official Music Video).mp4
```

Run the independent verifier as a second check:

```bash
/opt/nzyte-tv/app/nzytetv verify \
  "/srv/nzyte-tv/work/BroadcastReady/Lady Lady - Nzyte (Official Music Video).mp4"
```

The verified Pi run took 13 minutes 38 seconds for a 2 minute 27 second 2560x1440 source. It ended with:

```text
RESULT: BROADCAST READY
```

Do not use `--overwrite` unless replacing an existing destination is intentional. It never permits the source itself to be replaced. See [broadcast-standard.md](broadcast-standard.md) for the complete before/after measurements.

The temporary acceptance-test media was removed from `/srv/nzyte-tv/work` after testing. The work directory now contains only small reference/test metadata files.

## 12. Normalize the complete media library

The production batch command is:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library
```

It recursively finds `.mp4`, `.mov`, and `.mkv` files, preserves category folders, and verifies every output before counting it as ready. Run it inside a persistent SSH terminal multiplexer if the SSH connection may be interrupted; use of a particular multiplexer has not yet been standardized by this project.

The command is resumable. Without `--overwrite`, an existing destination is skipped only when its source-manifest fingerprint matches and independent verification passes. Changed sources, changed broadcast profiles, missing/corrupt manifests, and invalid existing destinations are normalized again. Individual normalization failures do not stop later files, and any remaining failure makes the final process exit code nonzero.

Use `--overwrite` only when all existing destinations should be deliberately regenerated:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library \
  --overwrite
```

Ctrl+C cancels the active conversion. The active temporary file is removed instead of being published as its final destination. Previously completed files remain in place and will be verified and skipped on the next run.

### Recorded 39-file production run

The full source set occupies approximately 7.3 GB and has a combined runtime of 1:17:09 (4,630 seconds). Production results were:

| Result | First run | Unchanged second run |
|---|---:|---:|
| Discovered | 39 | — |
| Normalized | 39 | 0 |
| Skipped existing | — | 39 |
| Failed | 0 | 0 |
| Verified ready | 39 | 39 |
| Manifests | 39 | 39 existing |
| Elapsed | Not yet supplied | Not yet supplied |

Every source completed normalization and independent verification. The unchanged second run re-verified and skipped all 39 destinations. The exact elapsed times and final normalized-library size remain to be recorded.

### Recorded nine-file acceptance run

A representative batch of five music videos and four vlog episodes completed on the Raspberry Pi:

```text
Discovered video files: 9
Normalized:             9
Skipped existing:       0
Failed:                 0
Verified ready:         9
Elapsed:             01:31:57
```

The unchanged second run independently re-verified and skipped every destination:

```text
Discovered video files: 9
Normalized:             0
Skipped existing:       9
Failed:                 0
Verified ready:         9
Elapsed:             00:00:57
```

These runs predate source-manifest tracking. The first run with the manifest-aware build will safely normalize the existing nine outputs again to create trusted sidecars. Later unchanged runs can use the fingerprint-plus-verification skip path.

### Recorded YouTube Live acceptance

Three normalized outputs were manually streamed from this Pi to YouTube Live using FFmpeg `-c copy`, FLV, and RTMPS ingest:

- `Nzyte - CASH RULES (Official Music Video).mp4`
- `Nzyte Vlog Episode 3.mp4`
- `Nzyte - American Dreams (Official Video).mp4`

All three streamed successfully with correct audio, aspect ratio, and A/V synchronization at approximately `speed=1.00x`. YouTube reported **Excellent** stream health for each file.

When manually stopping a live FLV/RTMP stream, these warnings can appear and are harmless in this context:

```text
Failed to update header with correct duration.
Failed to update header with correct filesize.
```

A future broadcaster command may suppress them with:

```text
-flvflags no_duration_filesize
```

This is acceptance-test documentation only. NZYTE TV v0.1 does not implement streaming or retain a YouTube stream key.

## Not yet tested

- AnyDesk without an active physical monitor.
- A dummy HDMI plug as a replacement for the physical monitor.
- Using `h264_v4l2m2m` to create files accepted by NZYTE TV verification.
- Dedicated long-duration thermal measurements for the completed 39-file `normalize-library` production run.
- Continuous production operation, scheduling, streaming, or service supervision.
