# Media library and normalization workflow

This document records the production media layout and defines safe handling rules for normalization and media scanning. For the complete novice sequence through live operation, start with the [operations runbook](operations-runbook.md).

## Storage architecture

The current portable acceptance used an exFAT USB drive moved from Windows to the Raspberry Pi. Physical device names can change; applications use the stable mount:

| Property | Production value |
|---|---|
| Device | Discover with `lsblk -f`; do not assume `/dev/sda1` |
| Filesystem | exFAT in the current portable acceptance |
| Identity | Mount by the drive's actual UUID |
| Mount point | `/srv/nzyte-tv/media` |
| Mount behavior | systemd automount |

The Raspberry Pi OS partition is `/dev/mmcblk0p2`, uses ext4, and provides approximately 29 GB. Keep Ubuntu, the application, repository, catalog, logs, playlists, and working data on the SD card. Keep large source and normalized media on the external drive.

The permanent physical layout is:

```text
/srv/nzyte-tv/media/
|-- source/                    original/master media
|-- library/                   normalized and verified broadcast assets
`-- System Volume Information/ Windows filesystem metadata; ignore
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
|   |-- Visualizers/
|   |-- Animated Visuals/
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

The source folders come from the authoritative directory/category mapping used by metadata discovery. `Visualizers` maps to the song-based `visualizer` type, and `Animated Visuals` maps to the song-based `animated-visual` type. `short-form` remains a supported programming type, but `media init` does not create a dedicated `Short Form` directory.

`media init` is also the safe layout-upgrade command. When it is rerun on a valid existing media root, it creates missing layout directories. It does not bump or replace the schema-version-1 descriptor, and it does not overwrite existing source media, library media, catalog files, manifests, programming sidecars, playlists, or history.

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

### Upgrade an existing portable drive

An existing portable drive can be extended without rebuilding it. After installing the upgraded NZYTE TV executable:

1. Run `nzytetv media init "E:\"`. Only missing layout paths are created, including `E:\source\Visualizers\` and `E:\source\Animated Visuals\`.
2. Copy full-song static or lightly animated graphical presentations into `E:\source\Visualizers\`.
3. Copy animated, narrative, cinematic, anime/movie-style, or AI-animated song presentations into `E:\source\Animated Visuals\`.
4. Normalize the complete source tree. No category-specific encoding rule is used:

   ```powershell
   nzytetv normalize-library `
     "E:\source" `
     "E:\library" `
     --vertical-layout blurred-background
   ```

5. Initialize and review metadata, synchronize reviewed changes, and build the playlist:

   ```powershell
   nzytetv metadata initialize `
     "E:\source" `
     "E:\library" `
     --catalog "E:\catalog\song-catalog.json"

   nzytetv metadata review `
     "E:\source" `
     "E:\library" `
     --catalog "E:\catalog\song-catalog.json"

   nzytetv metadata sync "E:\source" "E:\library"

   nzytetv build-playlist `
     "E:\library" `
     --catalog "E:\catalog\song-catalog.json" `
     --output "E:\playlists\current.json" `
     --history "E:\playlists\history.json" `
     --duration 6h
   ```

All visual forms of one recording use separate `assetId` values but the same catalog `contentGroupId`. Adding these directories does not provide song identity by itself; ambiguous or unresolved filenames still require metadata review.

The catalog, `.nzytetv.meta.json` programming sidecars, `.nzytetv.json` technical manifests, and any playlist/history JSON can travel on the media drive. The application executable and repository do not need to. Keeping catalog/playlists on the Pi's SD card remains supported; all commands accept explicit paths.

## Source and library rules

`/srv/nzyte-tv/source` contains original/master media. Normalization must never modify or replace these files.

`/srv/nzyte-tv/library` contains only NZYTE TV-normalized assets that passed automatic independent verification. Playlist and broadcaster components consume media from `/srv/nzyte-tv/library`, never from `/srv/nzyte-tv/source`.

The category folders are:

```text
Advertisements/
Animated Visuals/
Bumpers/
Interstitials/
Lyric Videos/
Music Videos/
Performance Videos/
Promos/
Specials/
Visualizers/
Vlog Episodes/
```

Folder names, spaces, and capitalization are intentional. Preserve them exactly. The remaining empty categories also remain part of the production layout so future station programming can use them.

`Visualizers` contains full-song still, static, or lightly animated graphical presentations. `Animated Visuals` contains animated, narrative, cinematic, anime/movie-style, AI-animated, or other extended song visuals. Both are ordinary song-based music programming, not cadence content, and must resolve through the song catalog before they are playlist eligible.

`Performance Videos` always initializes new metadata with the top-level `performance` type. Filename descriptors such as `Lipsync`, `Lip Sync`, `MicDrop`, and `Mic Drop` are retained as `lipsync` or `mic-drop` subtypes; they never silently turn a directory-backed performance into `short-form`. There is intentionally no `Short Form` source folder: short-form may be assigned through explicit metadata. Existing explicit programming sidecars are authoritative and are not rewritten merely because inference rules change.

`System Volume Information` is Windows filesystem metadata, not a media category. Ignore it; do not inspect, normalize, move, or delete it.

## Adding media during station operation

> **Preparing new content does not always require stopping the station. Physically removing or disrupting the USB that FFmpeg is reading does.**

The running broadcaster uses the normalized files named by the playlist queue supplied at startup. It does not watch the source, library, or playlist directories. Work on another computer or drive can continue without affecting that queue, but new assets will not appear in it automatically.

For a production USB that remains mounted on the Pi:

- adding a brand-new source or library path does not change the active queue;
- changing a source master alone does not change the normalized file already in the queue, but rerunning normalization may replace that stale destination;
- replacing, renaming, or removing an existing library file referenced by that queue is unsafe—stop the broadcaster first; and
- normalization alone never rewrites an existing playlist JSON.

The current single-USB workflow stores source, library, catalog, playlists, and history on the drive mounted at `/srv/nzyte-tv/media`. Before taking that physical drive to Windows:

Bash:

```bash
tmux attach -t nzyte-tv
# Press Ctrl+C inside tmux, then run:
pgrep -a ffmpeg
sudo umount /srv/nzyte-tv/media
findmnt /srv/nzyte-tv/media
```

`pgrep` should print nothing. With systemd automount, `findmnt` may still show an `autofs`/`systemd-1` trigger, but the real exFAT filesystem must not remain actively mounted. Do not access the mountpoint again before removal because doing so may remount it. Never unplug a mounted or in-use production USB.

On Windows, find the current drive letter, copy masters into `<drive>:\source\<category>\`, and normalize through NZYTE TV rather than manually populating `library/`:

PowerShell template:

```powershell
Get-Volume
nzytetv.exe normalize-library `
  "<drive>:\source" `
  "<drive>:\library" `
  --vertical-layout blurred-background
```

Current verified outputs skip; new, stale, or invalid outputs normalize. Complete catalog and metadata processing and safely eject the drive. After returning it to the Pi, verify it with `lsblk -f`, `ls /srv/nzyte-tv/media`, and `findmnt /srv/nzyte-tv/media`. Running `nzytetv media init /srv/nzyte-tv/media` is an optional safe layout check, not a required step for every content addition.

Build future playlist blocks with the current history file if the new asset should enter rotation. History records planned scheduling when playlists are generated; it is not a live playback-position database. Prefer a deliberate maintenance boundary before replacing planned blocks. See [Adding media while NZYTE TV is running](operations-runbook.md#adding-media-while-nzyte-tv-is-running) for the complete stop, move, return, playlist, and restart procedure.

## Production inventory acceptance example

At one recorded acceptance point, the portable production library contained 232 assets spanning animated visuals, promos, vlogs, short-form presentations, lyric videos, visualizers, performances, and music videos. All 232 normalized with 0 failures; an unchanged integrity pass skipped and independently verified all 232. Metadata initialization resolved the complete inventory without errors.

This is historical test context, not a permanent inventory contract. Operators should trust the current discovery, normalization, metadata, and dry-run summaries rather than expecting a fixed asset count.

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

### Completed production acceptance examples

A later portable-library acceptance discovered and normalized 232 source files with 0 failures. An unchanged integrity run normalized 0, skipped all 232, and independently verified all 232 ready. These figures demonstrate the workflow at that point in time; 232 is not a permanent station size.

An earlier 39-file production run recorded detailed Raspberry Pi timings:

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

A separate representative acceptance batch of nine production files previously completed normalization and verification successfully. These older results are retained as historical evidence, not as current inventory requirements.

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

Because the drive uses `x-systemd.automount`, access to `/srv/nzyte-tv/media` can be required before `findmnt` shows the real portable filesystem instead of `systemd-1`/`autofs`.

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
