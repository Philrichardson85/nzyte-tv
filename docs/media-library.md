# Media library and normalization workflow

This document records the production media layout established on the Raspberry Pi and defines safe handling rules for normalization and future media-scanning work.

## Storage architecture

The permanent media location is a SanDisk Cruzer Glide USB drive:

| Property | Production value |
|---|---|
| Device | `/dev/sda1` |
| Filesystem | NTFS3 |
| Approximate capacity | 30 GB |
| Mount point | `/srv/nzyte-tv/media` |
| Mount behavior | systemd automount |

The Raspberry Pi OS partition is `/dev/mmcblk0p2`, uses ext4, and provides approximately 29 GB. Keep Ubuntu, the application, repository, catalog, logs, playlists, and working data on the SD card. Keep large source and normalized media on the external drive.

The permanent physical layout is:

```text
/srv/nzyte-tv/media/
|-- source/                    original/master media
|-- library/                   normalized and verified broadcast assets
`-- System Volume Information/ Windows/NTFS metadata; ignore
```

Application and operator commands should use these convenience symlinks:

```text
/srv/nzyte-tv/source  -> /srv/nzyte-tv/media/source
/srv/nzyte-tv/library -> /srv/nzyte-tv/media/library
```

The persistent mount and blank-device setup are documented in [raspberry-pi-setup.md](raspberry-pi-setup.md). Do not format the existing media drive during application setup.

## Portable media-root initialization

On Windows or Linux, initialize a blank or partially populated removable drive with:

```text
nzytetv media init <media-root>
```

Examples:

```powershell
nzytetv media init "E:\"
```

```bash
nzytetv media init /mnt/nzyte-media
```

The command uses platform-neutral filesystem APIs. It does not format or partition the device, copy media, invoke FFmpeg/FFprobe, normalize, initialize metadata, or build playlists. It creates only missing paths and files:

```text
<media-root>/
|-- .nzytetv-media-root.json
|-- source/
|   |-- Music Videos/
|   |-- Lyric Videos/
|   |-- Performance Videos/
|   |-- Vlog Episodes/
|   |-- Bumpers/
|   |-- Promos/
|   |-- Interstitials/
|   |-- Advertisements/
|   `-- Specials/
|-- library/
|-- catalog/
|   `-- song-catalog.json
|-- playlists/
`-- work/
```

The descriptor contains only `{ "schemaVersion": 1 }`; it stores no drive letter, mount path, machine identity, or secret. A valid existing descriptor is preserved byte-for-byte. A corrupt or unsupported descriptor stops initialization before the root is changed. A missing song catalog is created as the existing schema-version-1 catalog with an empty `songs` array. Any existing catalog, including a populated one, is preserved exactly.

The source folders come from the authoritative directory/category mapping used by metadata discovery. `short-form` remains a supported programming type and optional recognized folder, but `media init` does not create a dedicated `Short Form` directory. Visualizer and Animated Visual categories are not created in this version.

### New-drive workstation-to-Pi workflow

1. Initialize the drive on the workstation:

   ```powershell
   nzytetv media init "E:\"
   ```

2. Copy original masters into `E:\source\<correct category>\`.

3. Normalize directly from source to library:

   ```powershell
   nzytetv normalize-library `
     "E:\source" `
     "E:\library"
   ```

   For portrait social-media masters, use the existing one-pass layout:

   ```powershell
   nzytetv normalize-library `
     "E:\source" `
     "E:\library" `
     --vertical-layout blurred-background
   ```

4. Run metadata initialization/review with the drive's catalog and source/library paths:

   ```powershell
   nzytetv metadata initialize `
     "E:\source" `
     "E:\library" `
     --catalog "E:\catalog\song-catalog.json"

   nzytetv metadata review `
     "E:\source" `
     "E:\library" `
     --catalog "E:\catalog\song-catalog.json"
   ```

5. Safely eject the physical drive, attach it to the Raspberry Pi, and mount it (for example at `/srv/nzyte-tv/media`).

The portrait path creates no intermediate H.264 file: visual treatment, canonical broadcast normalization, verification, publication, and technical-manifest writing remain one pipeline. Landscape files continue through ordinary normalization. A drive-letter or mount-path change does not change `assetId`, `contentGroupId`, source-relative technical fingerprints, or media-root identity, so the Pi does not need to re-encode assets that are already current and verified.

The catalog, `.nzytetv.meta.json` programming sidecars, `.nzytetv.json` technical manifests, and any playlist/history JSON can travel on the media drive. The application executable and repository do not need to. Keeping catalog/playlists on the Pi's SD card remains supported; all commands accept explicit paths.

## Source and library rules

`/srv/nzyte-tv/source` contains original/master media. Normalization must never modify or replace these files.

`/srv/nzyte-tv/library` contains only NZYTE TV-normalized assets that passed automatic independent verification. Future broadcaster and playlist components must consume media from `/srv/nzyte-tv/library`, never from `/srv/nzyte-tv/source`.

The category folders are:

```text
Advertisements/
Bumpers/
Interstitials/
Lyric Videos/
Music Videos/
Performance Videos/
Promos/
Specials/
Vlog Episodes/
```

Folder names, spaces, and capitalization are intentional. Preserve them exactly. The remaining empty categories also remain part of the production layout so future station programming can use them.

`System Volume Information` is Windows/NTFS filesystem metadata, not a media category. Ignore it; do not inspect, normalize, move, or delete it.

## Current source inventory

The production source library currently contains 39 video files:

| Category group | Count |
|---|---:|
| Lyric Videos | 14 |
| Music Videos | 5 |
| Vlog-related videos | 20 |
| **Total** | **39** |

The source library occupies approximately 7.3 GB. Its combined runtime is 1:17:09, or 4,630 seconds.

The earlier format inventory found:

- all 39 inspected files used H.264 video and `yuv420p`;
- 31 files were 24 fps;
- 8 files were 30 fps;
- 17 files were 1920x1080;
- 21 files were 2560x1440;
- 1 file was 2628x1440.

The 2628x1440 file is `Vlog 2 episode 3.mp4`. It is a useful future scale-and-pad regression case because its aspect ratio does not exactly match 1920x1080. That specific regression test has not yet been automated.

## Media-file filtering

`normalize-library` recursively scans the source root. Its current case-insensitive allowlist is:

```text
.mp4
.mov
.mkv
```

It does not assume that every file beneath the source root is playable media. Current scanner behavior:

- skip `System Volume Information` and reparse-point/symbolic-link directories encountered below the root;
- ignore images, text files, and unsupported extensions;
- filter candidate files with the explicit, case-insensitive allowlist;
- preserve relative directories, filenames, spaces, capitalization, and Unicode;
- normalize and independently verify candidates rather than trusting an extension.

`.mp4` is verified with the current Raspberry Pi library and workflow. `.mov` and `.mkv` discovery and destination mapping are covered by automated tests, but representative Raspberry Pi production runs for those source containers have not yet been recorded. Add extensions only with representative tests.

## Production normalization workflow

The production command is:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library
```

It creates corresponding paths such as:

```text
Source:      /srv/nzyte-tv/source/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4
Destination: /srv/nzyte-tv/library/Music Videos/Lady Lady - Nzyte (Official Music Video).mp4

Source:      /srv/nzyte-tv/source/Vlog Episodes/Vlog 2 episode 3.mp4
Destination: /srv/nzyte-tv/library/Vlog Episodes/Vlog 2 episode 3.mp4
```

The workflow has these safety and resumability rules:

- source and destination roots must be separate and non-overlapping;
- relative category directories are preserved;
- `.mov` and `.mkv` destination extensions become `.mp4`;
- normalization uses temporary output and publishes the final destination only after verification passes;
- each successful destination receives a `<output>.mp4.nzytetv.json` source-manifest sidecar;
- a destination is skipped only when its source fingerprint matches and independent verification passes;
- a changed source, changed broadcast profile, missing/corrupt manifest, or invalid destination causes re-normalization;
- failures are recorded without aborting later files;
- colliding source names such as `name.mov` and `name.mkv` in one category fail rather than overwrite the same `name.mp4` destination;
- Ctrl+C cancels the active file, while temporary-output handling prevents a partial final destination.

### Portrait and 9:16 source workflow

Portrait masters remain in the normal source tree and can be converted during the existing one-pass normalization workflow:

```bash
/opt/nzyte-tv/app/nzytetv normalize-library \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library \
  --vertical-layout blurred-background
```

For a single file:

```bash
/opt/nzyte-tv/app/nzytetv normalize \
  "/srv/nzyte-tv/source/Short Form/portrait master.mp4" \
  --vertical-layout blurred-background
```

The option is explicit. FFprobe display dimensions, sample aspect ratio, and rotation metadata determine whether a source is portrait. Portrait video is centered without stretching at full 1080-pixel height. A second copy fills and crops the 1920x1080 background, is blurred, darkened by `-0.18`, and desaturated to `0.70`. Landscape files use the existing scale-and-pad filter. If display orientation cannot be determined safely, that file fails clearly while later batch files continue.

This replaces the separate PowerShell `16x9 Output` conversion tree for normal NZYTE TV ingest. The original portrait file belongs under `source/`; the verified 16:9 result belongs under the same relative path in `library/`. A custom hand-designed 16:9 export may instead be placed in `source/` and normalized normally without `--vertical-layout`.

The selected layout is stored in the technical source manifest. Requesting blurred-background after an ordinary normalization, or returning to ordinary normalization later, invalidates the resumable skip and rebuilds the destination. Legacy manifests without this field are treated as ordinary mode, avoiding unnecessary regeneration of existing landscape assets.

#### Manual PowerShell/FFmpeg visual reference

The application command above is the supported ingest path because it combines the visual transformation, canonical broadcast encoding, verification, atomic publication, and manifest update in one operation. The former manual recipe is retained only as a reference/fallback for reproducing the visual composition:

```powershell
$filter = @"
[0:v]split=2[bgsrc][fgsrc];
[bgsrc]scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,boxblur=30:15,eq=brightness=-0.18:saturation=0.70[bg];
[fgsrc]scale=-2:1080[fg];
[bg][fg]overlay=(W-w)/2:(H-h)/2[outv]
"@

ffmpeg -i $InputPath -filter_complex $filter -map "[outv]" -map "0:a?" $ReferenceOutput
```

That reference command does not replace NZYTE TV normalization or verification. A manually produced file must still enter the standard source/library workflow before it is broadcast eligible.

Do not use `--overwrite` unless deliberate regeneration of existing destinations is required. It replaces destinations only and never authorizes replacement of source masters.

### Completed production run

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

All source files completed normalization and independent verification. The unchanged second run re-verified every matching destination and skipped all 39, confirming production-library resumability and source-manifest matching. Final validation found 39 broadcast-ready videos, 39 matching source manifests, and a 3.3 GB normalized library.

After normalization, the external drive reported 30 GB total, 11 GB used, 19 GB available, and 36% utilization. All large source and normalized media remain on that drive under `/srv/nzyte-tv/media/source` and `/srv/nzyte-tv/media/library`; the stable application paths remain the `/srv/nzyte-tv/source` and `/srv/nzyte-tv/library` symlinks.

These results complete the v0.1 media-normalization and verification milestone and production-validate it on the Raspberry Pi 4 / `linux-arm64` deployment.

A separate representative acceptance batch of nine production files previously completed normalization and verification successfully. That result validates the batch workflow but is not a result for the current 39-file run.

## Work-directory cleanup

Temporary acceptance-test media under `/srv/nzyte-tv/work` was removed after testing. The work directory now contains only small reference/test metadata files. It remains available for working data, but the permanent normalized library is `/srv/nzyte-tv/library`.

## Symlinks and maintenance commands

Some commands do not follow the `/srv/nzyte-tv/source` and `/srv/nzyte-tv/library` symlinks by default. Use `-L` when the intent is to inspect their target content:

```bash
du -shL /srv/nzyte-tv/source
du -shL /srv/nzyte-tv/library
find -L /srv/nzyte-tv/source -type f
find -L /srv/nzyte-tv/library -type f
```

To confirm the links themselves and the underlying mount:

```bash
ls -ld /srv/nzyte-tv/source /srv/nzyte-tv/library
findmnt /srv/nzyte-tv/media
```

Because the drive uses `x-systemd.automount`, access to `/srv/nzyte-tv/media` can be required before `findmnt` shows the real NTFS3 mount instead of `systemd-1`/`autofs`.

## Quoting paths on Linux

Always quote media paths because category and filenames contain spaces:

```bash
/opt/nzyte-tv/app/nzytetv inspect \
  "/srv/nzyte-tv/source/Vlog Episodes/Vlog 2 episode 3.mp4"
```

The application passes paths directly to FFmpeg and FFprobe without constructing a shell command. Scripts that call the application must still quote each shell argument correctly.

## Source manifests and migration

A source manifest records:

- manifest schema version;
- source-relative path;
- source size;
- source last-modified UTC;
- broadcast-profile version;
- requested vertical-layout mode (`none` or `blurred-background`).

The manifest is written atomically beside the normalized file after output verification passes. If the source changes during normalization, the batch records a failure and does not mark that output as current.

Outputs created by older versions do not have manifests. The first run after upgrading conservatively normalizes those files again and creates sidecars. The application does not adopt an existing output based only on technical validity because it cannot prove that the output came from the current source master.

The required media format is defined in [broadcast-standard.md](broadcast-standard.md).

Programming identity and scheduling metadata are separate from normalization. The master song catalog, `.nzytetv.meta.json` sidecars, initialization/review/synchronization commands, and eligibility rules are defined in [content-catalog.md](content-catalog.md). Introducing or changing programming metadata never requires these 39 production assets to be encoded again.
