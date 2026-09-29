# NZYTE TV operations runbook

This is the primary start-to-finish guide for operating NZYTE TV. It assumes no prior Git, .NET, FFmpeg, PowerShell, Linux, tmux, or NZYTE TV experience. Follow the steps in order for a new installation; use the quick-reference sections later for routine work.

For deeper technical detail, see the focused guides linked in [Further reading](#further-reading).

## 1. What NZYTE TV does

The complete flow is:

```text
source masters
    ↓
normalize
    ↓
broadcast-ready library
    ↓
metadata + song catalog
    ↓
playlist generation
    ↓
playlist history
    ↓
broadcast
    ↓
FFmpeg stream-copy
    ↓
YouTube Live
```

The commands have four broad responsibilities:

- **Normalize** is technical media preparation. It converts source masters into the one tested H.264/AAC broadcast format and verifies the result.
- **Metadata and catalog** describe content identity and programming information. They answer “what asset is this?” and “which song does it represent?”
- **Build playlist** is television programming. It creates a deterministic, airtime-aware schedule and preserves cooldown state in history.
- **Broadcast** is playback and transmission. It plays supplied playlists sequentially and sends already-normalized media to RTMP/RTMPS with FFmpeg stream-copy.

These responsibilities are deliberately separate. Metadata never encodes media. Playlist generation never changes media. Broadcast never repairs or transcodes media.

## 2. Safety rules before starting

1. Never put a YouTube stream key, password, GitHub token, or other credential in Git, documentation, a playlist, or a command argument.
2. Keep original masters under `source/`. NZYTE TV writes normalized outputs under `library/`; never use manual copying into `library/` as a substitute for normalization.
3. Stop an active broadcast before changing the USB mount, replacing the production application, or rebooting the Pi.
4. Do not edit `/etc/fstab` until `lsblk -f` confirms the correct drive UUID and filesystem.
5. If `git status --short` shows changes you do not understand, stop before switching branches or pulling.
6. If a validation command reports an error, stop and fix it rather than continuing to the live broadcast step.

## 3. PowerShell and Bash are different

Commands below are labeled **PowerShell** for Windows or **Bash** for Linux/Raspberry Pi. Do not mix them.

- PowerShell continues a command with a backtick: `` ` ``
- Bash continues a command with a backslash: `\`

The continuation character must be the final character on its line—no trailing spaces.

## 4. Source code is not the runnable application

This distinction prevents a common update mistake:

```text
git fetch / git pull / git switch
    update SOURCE CODE

dotnet publish
    creates or updates the RUNNABLE APPLICATION
```

Pulling new source does **not** magically update these published applications:

- Windows: `C:\Tools\NzyteTv\...`
- Raspberry Pi/Linux: `/opt/nzyte-tv/app`

After changing the checked-out branch or tag, publish again and run `--help` from the exact new output path.

`main` means the latest integrated code. A tag such as `vX.Y.Z` is a frozen, reproducible release. Replace `vX.Y.Z` with a real tag that exists; do not type the placeholder literally.

## 5. Command reference: what to run and when

| Command | What it does | When to use it |
|---|---|---|
| `inspect <file>` | Reads technical media characteristics without modifying the file. | Diagnose an unfamiliar source or output. |
| `normalize <file>` | Normalizes one media file and verifies it. | Test or process one asset. |
| `normalize-library <source> <library>` | Scans the source tree; verifies current outputs and normalizes new, stale, or invalid files. | Initial ingest and every later content update. |
| `verify <file>` | Independently checks broadcast compliance. | Confirm one normalized output. |
| `media init <root>` | Safely creates or upgrades the portable NZYTE TV directory structure. It does not format the drive. | New USB drive or layout upgrade. |
| `metadata initialize` | Creates/preserves programming sidecars and resolves songs against the catalog. | After normalization or after adding content/catalog entries. |
| `metadata review` | Prompts a human to resolve ambiguous or unresolved song identity. | When initialization reports review work. |
| `metadata sync` | Copies source programming sidecars to matching normalized library assets without encoding. | After metadata edits or review. |
| `metadata rebind` | Preserves asset identity after an intentional source rename or move. | Before treating a renamed source as a new asset. |
| `metadata edit` | Manually changes programming type/subtype without encoding. | Correct classification, including performance subtypes. |
| `build-playlist` | Creates a deterministic airtime-aware schedule and optionally updates history. | After the eligible library changes or another programming block is needed. |
| `broadcast` | Validates playlist files, concatenates normalized assets, and stream-copies them to RTMP/RTMPS using FFmpeg. | Dry-run first; then start the live stream. |
| `station validate` | Validates non-secret station configuration and the existing broadcast readiness checks without launching FFmpeg. | Before every station/service start or after configuration changes. |
| `station run` | Supervises the configured fixed queue through the existing resilient broadcaster and writes runtime state/heartbeat. | Checkpoint 1 manual production operation. |
| `station status` | Reads runtime state and verifies heartbeat freshness and station PID without displaying process arguments. | Check station, broadcast, FFmpeg, media, and queue state. |

Use `nzytetv <command> --help` for command-specific syntax.

## 6. Prepare a new Windows workstation

### 6.1 Install and check prerequisites

NZYTE TV development requires Git, the .NET 10 SDK, FFmpeg, and FFprobe. FFprobe normally comes with FFmpeg.

PowerShell:

```powershell
git --version
dotnet --version
ffmpeg -version
ffprobe -version
```

`dotnet --version` must begin with `10.`. If a command is missing, follow [Workstation setup](workstation-setup.md) and reopen PowerShell after installation.

### 6.2 Clone the repository on a new computer

PowerShell:

```powershell
New-Item -ItemType Directory -Force C:\_Code | Out-Null
Set-Location C:\_Code
git clone https://github.com/Philrichardson85/nzyte-tv.git
Set-Location C:\_Code\nzyte-tv
git status --short
```

No output from `git status --short` means the checkout is clean. Stop if it reports unexpected changes.

### 6.3 Update an existing checkout to `main`

PowerShell:

```powershell
Set-Location C:\_Code\nzyte-tv
git status --short
git fetch origin
git switch main
git pull --ff-only origin main
```

For a frozen production release instead:

PowerShell:

```powershell
Set-Location C:\_Code\nzyte-tv
git status --short
git fetch origin --tags
git switch --detach vX.Y.Z
```

Stop if the requested tag does not exist or Git reports local changes. Do not guess at conflict resolution on a production checkout.

### 6.4 Build and test

PowerShell:

```powershell
dotnet restore NzyteTv.slnx
dotnet build NzyteTv.slnx -c Release
dotnet test NzyteTv.slnx -c Release --no-build
```

Continue only if the build and tests succeed.

### 6.5 Publish the Windows x64 application

Publish to a candidate directory rather than overwriting a known-good executable immediately.

PowerShell:

```powershell
dotnet publish `
  src\NzyteTv.Cli\NzyteTv.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o C:\Tools\NzyteTv\candidate

C:\Tools\NzyteTv\candidate\nzytetv.exe --help
```

The backtick is PowerShell's multiline continuation. Bash uses `\`, not a backtick. A self-contained publish includes the .NET runtime for running NZYTE TV; FFmpeg and FFprobe remain external tools.

## 7. Prepare the portable media drive on Windows

This runbook uses `E:` as an example. Windows may assign a different letter on another computer. Confirm the drive in File Explorer or with `Get-Volume`; never format a drive merely because the expected letter changed.

PowerShell:

```powershell
Get-Volume
Set-Location C:\Tools\NzyteTv\candidate
.\nzytetv.exe media init E:\
```

`media init` creates missing structure without formatting the drive or replacing existing content:

```text
E:\
├── source\
│   ├── Music Videos\
│   ├── Lyric Videos\
│   ├── Performance Videos\
│   ├── Visualizers\
│   ├── Animated Visuals\
│   ├── Vlog Episodes\
│   ├── Bumpers\
│   ├── Promos\
│   ├── Interstitials\
│   ├── Advertisements\
│   └── Specials\
├── library\
├── catalog\
│   └── song-catalog.json
├── playlists\
└── work\
```

- `source/` contains original masters.
- `library/` contains normalized broadcast-ready files managed by NZYTE TV.
- `catalog/song-catalog.json` defines stable song identity.
- `playlists/` contains generated schedules and history.
- `work/` is scratch/working space.

Never manually place a file in `library/` to pretend it was normalized. The technical manifest and independent verification are part of readiness.

## 8. Normalize the media library

Copy masters into the appropriate category under `E:\source`, then run the batch command.

PowerShell:

```powershell
Set-Location C:\Tools\NzyteTv\candidate
.\nzytetv.exe normalize-library `
  "E:\source" `
  "E:\library" `
  --vertical-layout blurred-background
```

The command:

- leaves every source master untouched;
- normally normalizes landscape media to the tested broadcast standard;
- applies the existing one-pass blurred-background 16:9 treatment to portrait media;
- independently verifies outputs before publishing them to `library/`;
- writes technical `.nzytetv.json` manifests; and
- re-verifies and skips current outputs on later runs.

The first run normally processes all new assets. An identical second run should verify and skip current assets. A historical production acceptance example normalized 232 files with 0 failures; an unchanged integrity run then skipped and verified all 232. That is an acceptance example, not a permanent required library size.

### Add new content later without re-encoding everything

Example: add one bumper.

1. Copy its original master into `E:\source\Bumpers\`.
2. Run the same `normalize-library` command.
3. Existing current files are independently verified and skipped.
4. The new bumper—and any stale or invalid output—is normalized.
5. Run the metadata workflow.
6. Build a new playlist.

Technical manifests and source-change detection mean you do **not** re-encode the entire station whenever one asset is added.

### Adding media while NZYTE TV is running

> **YOU DO NOT ALWAYS HAVE TO STOP THE STATION TO PREPARE NEW CONTENT.**
>
> You **do** have to stop it before physically removing the USB it is streaming from, replacing or moving a media file used by the active queue, or otherwise making the mounted library unavailable. You **do not** have to stop it merely to prepare media on another computer or drive, edit source material elsewhere, or plan future programming. New media never enters the active queue automatically.

The broadcaster reads normalized files from `/srv/nzyte-tv/media/library`. At startup it validates the specific playlist JSON files supplied on the command line and builds one FFmpeg concat sequence. It does not watch `source/`, `library/`, or `playlists/` for changes.

This creates five distinct situations:

1. **Preparing content elsewhere:** the Pi may keep broadcasting while another computer or drive receives and normalizes new masters.
2. **Removing the production USB:** stop the broadcaster and unmount first. FFmpeg is actively reading from that drive.
3. **Updating the mounted production USB:** adding a brand-new file at a path not used by the active queue does not dynamically affect playback. Editing a source master alone does not alter the normalized copy already playing, but a later normalization may replace its stale library destination. Never replace, rename, or remove an active library file while FFmpeg may read it; stop first.
4. **Generating future playlists:** this may be done while an existing queue runs, but the new JSON is only a future plan and history must be handled carefully.
5. **Using the new playlists:** restart the broadcaster later with those playlist paths. The running process does not discover them.

For the current single-USB production workflow, the same physical drive contains `source/`, `library/`, `catalog/`, `playlists/`, and history and is mounted at `/srv/nzyte-tv/media`. If that USB must go to the Windows i9 workstation, **yes—stop the station first**.

#### Safely remove the production USB

Prefer a convenient maintenance boundary, such as the end of a playlist block, when practical.

Bash:

```bash
tmux attach -t nzyte-tv
```

Inside tmux, press Ctrl+C once and allow NZYTE TV to shut down. Then check for FFmpeg:

Bash:

```bash
pgrep -x ffmpeg
```

The expected result is no output. If a PID is listed, do not unplug the drive; investigate the still-running process. Exit the inactive tmux shell with `exit`, or detach with Ctrl+B, release the keys, then press D.

Unmount and check the mount state:

Bash:

```bash
sudo umount /srv/nzyte-tv/media
findmnt /srv/nzyte-tv/media
```

With a systemd automount entry, `findmnt` may still show an `autofs`/`systemd-1` trigger. The real exFAT filesystem must no longer be actively mounted. Do not access files under the mountpoint after unmounting, because that can trigger automount again. Only then unplug the USB.

#### Update the USB on Windows

Use File Explorer or `Get-Volume` to find the current drive letter. The example below uses `<drive>` as a placeholder; replace it, for example with `E`.

PowerShell:

```powershell
Get-Volume

nzytetv.exe normalize-library `
  "<drive>:\source" `
  "<drive>:\library" `
  --vertical-layout blurred-background
```

First copy new original masters into `<drive>:\source\<category>\`. Never manually copy a supposed normalized output into `library/`. The batch independently verifies and skips existing current assets; only new, stale, or invalid media is normalized. Complete the required catalog and metadata initialize/review/edit/sync workflow, then use Windows **Safely Remove Hardware/Eject** before unplugging the drive.

#### Return the USB and prepare future programming

Reconnect the USB to the Pi, then inspect it:

Bash:

```bash
lsblk -f
ls /srv/nzyte-tv/media
findmnt /srv/nzyte-tv/media
```

Stop if the expected drive, filesystem, or portable-root directories are absent. This optional structural check is safe, but it is not required on every content update:

Bash:

```bash
/opt/nzyte-tv/app/nzytetv media init /srv/nzyte-tv/media
```

**Normalization does not automatically change an existing playlist.** Generate future block files if the new asset should enter rotation:

Bash template:

```bash
/opt/nzyte-tv/app/nzytetv build-playlist \
  /srv/nzyte-tv/media/library \
  --catalog /srv/nzyte-tv/media/catalog/song-catalog.json \
  --output /srv/nzyte-tv/media/playlists/<new-playlist>.json \
  --duration 6h \
  --seed <seed> \
  --history /srv/nzyte-tv/media/playlists/history.json
```

Replace both angle-bracket placeholders before running the command. Use the current history file for continuity.

History is updated when playlists are generated. It is programming/scheduling history, **not** a live database of which item FFmpeg has finished playing. Stopping midway through already-generated blocks and regenerating can therefore make planned history differ from what actually aired. In the current manual workflow, prefer completing the planned block or deliberately choosing a maintenance boundary before replacing future schedules.

After media and future playlists are ready, configure `NZYTE_TV_RTMP_URL` again using the silent procedure in [Configure the RTMPS destination safely](#15-configure-the-rtmps-destination-safely), then start a new queue:

Bash:

```bash
/opt/nzyte-tv/app/nzytetv broadcast \
  /srv/nzyte-tv/media/playlists/production-03.json \
  /srv/nzyte-tv/media/playlists/production-04.json \
  --library /srv/nzyte-tv/media/library
```

The broadcaster must be restarted with those paths—or receive them in another future invocation. It does not automatically notice new playlist JSON files.

## 9. Catalog and metadata

### 9.1 Understand the two identities

- `assetId` identifies one specific media asset.
- `contentGroupId` identifies the underlying song/recording.

A music video, lyric video, visualizer, animated visual, performance, and short song presentation of the same recording each have different `assetId` values but share one `contentGroupId`.

The folder supplies the default programming **type**. The catalog supplies song identity. A folder name alone never proves which song a file represents.

Files under `Performance Videos` remain top-level type `performance`. Descriptors can add subtypes such as `performance / lipsync` or `performance / mic-drop`; the subtype does not replace the performance type. Existing explicit metadata remains authoritative.

### 9.2 Initialize and review metadata

Preview first:

PowerShell:

```powershell
Set-Location C:\Tools\NzyteTv\candidate
.\nzytetv.exe metadata initialize `
  "E:\source" `
  "E:\library" `
  --catalog "E:\catalog\song-catalog.json" `
  --dry-run
```

Apply when the preview is understood:

PowerShell:

```powershell
.\nzytetv.exe metadata initialize `
  "E:\source" `
  "E:\library" `
  --catalog "E:\catalog\song-catalog.json"
```

If review is required:

PowerShell:

```powershell
.\nzytetv.exe metadata review `
  "E:\source" `
  "E:\library" `
  --catalog "E:\catalog\song-catalog.json"
```

Synchronize programming changes to normalized assets:

PowerShell:

```powershell
.\nzytetv.exe metadata sync "E:\source" "E:\library"
```

Correct a performance subtype without changing asset identity:

PowerShell:

```powershell
.\nzytetv.exe metadata edit `
  "E:\source\Performance Videos\Example Lipsync.mp4" `
  --type performance `
  --subtype lipsync

.\nzytetv.exe metadata sync "E:\source" "E:\library"
```

After an intentional rename or move, use `metadata rebind` so the asset does not accidentally receive a new identity:

PowerShell:

```powershell
.\nzytetv.exe metadata rebind `
  "E:\source\Music Videos\Old Name.mp4" `
  "E:\source\Music Videos\New Name.mp4"
```

See [Content catalog and asset metadata](content-catalog.md) before editing the catalog JSON manually.

## 10. Generate playlists and history

### 10.1 Dry-run programming first

Dry-run is useful for examining airtime balance. It writes no playlist and no history.

Bash template:

```bash
nzytetv build-playlist \
  <library> \
  --catalog <catalog> \
  --output <playlist.json> \
  --duration 6h \
  --seed <seed> \
  --dry-run
```

Replace every angle-bracket placeholder before running the template.

### 10.2 Generate a real block on the Pi

Bash:

```bash
/opt/nzyte-tv/app/nzytetv build-playlist \
  /srv/nzyte-tv/media/library \
  --catalog /srv/nzyte-tv/media/catalog/song-catalog.json \
  --output /srv/nzyte-tv/media/playlists/production-01.json \
  --duration 6h \
  --seed 20260928 \
  --history /srv/nzyte-tv/media/playlists/history.json
```

The seed is any chosen 32-bit integer. Reusing the same eligible library, history, time, policy, and seed makes candidate ordering reproducible.

`history.json` carries asset and same-song cooldown state across playlist boundaries. Generate the next block with the **same current history file**:

Bash:

```bash
/opt/nzyte-tv/app/nzytetv build-playlist \
  /srv/nzyte-tv/media/library \
  --catalog /srv/nzyte-tv/media/catalog/song-catalog.json \
  --output /srv/nzyte-tv/media/playlists/production-02.json \
  --duration 6h \
  --seed 20260929 \
  --history /srv/nzyte-tv/media/playlists/history.json
```

Do not casually delete or reset history while operating a station; doing so discards cross-playlist cooldown context.

### 10.3 Configured and effective airtime targets

**Configured targets** are the desired station mix. They remain:

| Normal-program type | Configured target |
|---|---:|
| music-video | 20% |
| lyric-video | 15% |
| visualizer | 15% |
| animated-visual | 20% |
| performance | 10% |
| short-form | 5% |
| vlog | 15% |

**Effective targets** are the attainable mix for this playlist horizon and current eligible inventory. The scheduler estimates practical replay capacity from eligible durations and the exact-asset cooldown preference, caps impossible targets, and redirects unavailable airtime to music-oriented categories before vlog. This does not rewrite the configured percentages; a larger future library automatically changes the effective plan.

Promo, bumper, and interstitial are cadence-controlled and sit outside the normal-program percentage mix.

### 10.4 Song pacing in plain language

All song presentations sharing a `contentGroupId` use one same-song clock.

For a full song followed by another full song:

- 90 minutes or more is preferred;
- 60–90 minutes is a controlled fallback;
- 45–60 minutes is a music-rescue range used only when needed;
- less than 45 minutes is emergency behavior.

Directional short-presentation spacing is:

| Transition | Preferred | Normal floor |
|---|---:|---:|
| short → short | 15 minutes | 10 minutes |
| full → short | 30 minutes | 20 minutes |
| short → full | 30 minutes | 15 minutes |

The exact same asset has a separate approximately two-hour replay preference. A relaxed song-group preference never means the identical clip should repeat immediately.

### 10.5 Cadence content is not filler

- Promo: blocked below 30 minutes, preferred at 30–45 minutes, overdue after 45 minutes.
- Interstitial: blocked below 20 minutes, preferred at 20–30 minutes, overdue after 30 minutes.
- Bumper: requires at least four normal programs before insertion.

These assets cannot become generic filler merely because another category is constrained. See [Playlist and programming engine](playlists.md) for full policy details.

## 11. Move the USB drive from Windows to Raspberry Pi

Safely eject the drive in Windows before unplugging it.

The same physical drive can appear as:

```text
Windows drive letter:  E:\
Linux block device:    /dev/sda1   (example only; this can change)
Stable Pi mountpoint:  /srv/nzyte-tv/media
```

Asset identity uses paths relative to the media root, not the Windows letter or Linux device name. Moving the USB does not require re-encoding current verified assets.

On the Pi, inspect rather than guess:

Bash:

```bash
lsblk -f
cat /etc/fstab
findmnt /srv/nzyte-tv/media
```

Stop if the filesystem, UUID, or device is not what you expect.

## 12. Mount the portable drive safely on the Pi

The tested portable workflow used an exFAT USB drive identified by UUID. Device names such as `/dev/sda1` may change; the stable application path remains `/srv/nzyte-tv/media`.

### 12.1 Stop broadcast and back up fstab

Bash:

```bash
sudo cp /etc/fstab /etc/fstab.backup
lsblk -f
```

Do not continue until you have copied the exact USB UUID and confirmed the filesystem type.

### 12.2 Edit only the media-drive line

Bash:

```bash
sudo mkdir -p /srv/nzyte-tv/media
sudoedit /etc/fstab
```

Generic exFAT example—replace `<USB-UUID>` before saving:

```fstab
UUID=<USB-UUID> /srv/nzyte-tv/media exfat defaults,uid=1000,gid=1000,umask=022,nofail,x-systemd.automount 0 0
```

Do **not** paste that line unchanged. Confirm the correct filesystem and user/group IDs for the Pi. Edit only the media-drive entry; leave system partitions alone.

### 12.3 Reload and verify

Bash:

```bash
sudo systemctl daemon-reload
sudo mount -a
ls /srv/nzyte-tv/media
findmnt /srv/nzyte-tv/media
```

Stop if `mount -a` reports any error, if the expected `source`, `library`, `catalog`, and `playlists` directories are absent, or if `findmnt` points to the wrong filesystem.

If convenience symlinks are already used, verify them:

Bash:

```bash
ls -ld /srv/nzyte-tv/source /srv/nzyte-tv/library
readlink -f /srv/nzyte-tv/source
readlink -f /srv/nzyte-tv/library
```

They should resolve to `/srv/nzyte-tv/media/source` and `/srv/nzyte-tv/media/library`. If either path already exists but points elsewhere, stop; do not overwrite it blindly.

## 13. Update and publish the Raspberry Pi application

### 13.1 Update the source checkout

Bash:

```bash
cd /opt/nzyte-tv/src
git status --short
git fetch origin --tags
git switch main
git pull --ff-only origin main
```

For a frozen release instead:

Bash:

```bash
cd /opt/nzyte-tv/src
git status --short
git fetch --tags
git switch --detach vX.Y.Z
```

Again: this updates `/opt/nzyte-tv/src`, not `/opt/nzyte-tv/app`.

### 13.2 Publish an ARM64 candidate

**Warning:** the next removal targets only the fixed candidate directory. Verify the printed path before removing it. It must not be `/opt/nzyte-tv/app`, `/opt/nzyte-tv`, or the repository.

Bash:

```bash
ls -ld /opt/nzyte-tv/app-candidate 2>/dev/null || true
rm -rf /opt/nzyte-tv/app-candidate

cd /opt/nzyte-tv/src
dotnet publish \
  src/NzyteTv.Cli/NzyteTv.Cli.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o /opt/nzyte-tv/app-candidate
```

Validate the candidate before promotion:

Bash:

```bash
/opt/nzyte-tv/app-candidate/nzytetv --help
file /opt/nzyte-tv/app-candidate/nzytetv
```

On the Raspberry Pi, `file` should identify an ARM aarch64 executable. Candidate deployment is safer than publishing over the current app because the known-good production directory remains untouched during build and validation.

### 13.3 Preserve a manual rollback and promote

Use a real release/build label in place of `vX.Y.Z`. First inspect every path:

Bash:

```bash
cd /opt/nzyte-tv
ls -ld app app-candidate
ls -ld app-vX.Y.Z-rollback 2>/dev/null || true
```

Stop if the rollback path already exists; choose a unique name. Stop the active broadcaster, then promote:

Bash:

```bash
cd /opt/nzyte-tv
mv app app-vX.Y.Z-rollback
mv app-candidate app
/opt/nzyte-tv/app/nzytetv --help
```

If the promoted app fails validation, manual rollback is:

Bash:

```bash
cd /opt/nzyte-tv
mv app app-candidate-failed
mv app-vX.Y.Z-rollback app
/opt/nzyte-tv/app/nzytetv --help
```

Never blindly delete rollback directories. Rollback is intentionally manual at this stage.

## 14. Validate the broadcast queue

Always dry-run the exact queue before going live.

Bash:

```bash
/opt/nzyte-tv/app/nzytetv broadcast \
  /srv/nzyte-tv/media/playlists/production-01.json \
  /srv/nzyte-tv/media/playlists/production-02.json \
  --library /srv/nzyte-tv/media/library \
  --dry-run
```

Dry-run validates:

- playlist files and JSON;
- supported playlist schema;
- sequence ordering;
- media paths and path containment;
- referenced MP4 existence;
- technical normalization manifests; and
- FFmpeg availability.

It does **not** launch FFmpeg, require an RTMP/RTMPS destination, or broadcast.

A historical broadcaster acceptance dry-run reported 2 playlists, 650 scheduled items, approximately 12 hours, and 0 missing, invalid, or unready assets. Those counts describe that tested queue only. Continue only when your own output says `READY` with zero problems.

## 15. Configure the RTMPS destination safely

Typing this is discouraged:

```text
export NZYTE_TV_RTMP_URL="...secret..."
```

It may place the secret in shell history. Instead, read it silently in the shell that will run the broadcaster.

Bash:

```bash
read -rsp "Paste full YouTube RTMPS URL: " NZYTE_TV_RTMP_URL
echo
export NZYTE_TV_RTMP_URL
```

- `read` reads a value.
- `-r` treats backslashes as ordinary characters.
- `-s` is silent, so the pasted secret is not displayed.
- `-p` displays the prompt.

The value conceptually looks like `rtmps://.../live2/<SECRET-KEY>`. Never put a real value in documentation or Git.

Check only whether it exists:

Bash:

```bash
if [ -n "$NZYTE_TV_RTMP_URL" ]; then
    echo "Broadcast destination configured"
else
    echo "Broadcast destination NOT configured"
fi
```

Never print the value of `NZYTE_TV_RTMP_URL`.

Environment variables are safer than command arguments. The Checkpoint 1 systemd workflow uses the root-controlled `/etc/nzyte-tv/secrets.env` described in the [station service guide](station-service.md). When finished with a manual shell session:

Bash:

```bash
unset NZYTE_TV_RTMP_URL
```

### Refreshing a changed YouTube destination

The broadcaster inherits environment values when it starts. Changing `NZYTE_TV_RTMP_URL` in another shell does **not** update an already-running broadcast. To use a refreshed YouTube RTMPS destination or token:

1. Stop the broadcaster cleanly with Ctrl+C.
2. Clear the old shell value if appropriate:

   ```bash
   unset NZYTE_TV_RTMP_URL
   ```

3. Read and export the replacement securely:

   ```bash
   read -rsp "Paste full YouTube RTMPS URL: " NZYTE_TV_RTMP_URL
   echo
   export NZYTE_TV_RTMP_URL
   ```

4. Restart `/opt/nzyte-tv/app/nzytetv broadcast ...` from that shell or tmux pane.

Never print the value or include a real destination or stream key in documentation, commands, logs, or screenshots.

### Safe FFmpeg process checks

NZYTE TV redacts the destination from application logs, but FFmpeg receives the RTMP/RTMPS destination as a process argument. A full process command line can expose the stream credential. Do not display FFmpeg command lines or print the destination variable.

Check whether FFmpeg is running:

```bash
pgrep -x ffmpeg
```

This prints only matching PID(s). Expected during normal broadcasting: one PID.

Count matching processes:

```bash
pgrep -x -c ffmpeg
```

Expected during normal broadcasting: `1`.

## 16. Start and stop a live broadcast

### Start

Bash:

```bash
/opt/nzyte-tv/app/nzytetv broadcast \
  /srv/nzyte-tv/media/playlists/production-01.json \
  /srv/nzyte-tv/media/playlists/production-02.json \
  --library /srv/nzyte-tv/media/library
```

Playlist order is preserved, and items follow playlist `sequence`. One FFmpeg process uses the concat demuxer across file transitions. `-re` provides real-time pacing. `-c copy` means no transcoding: normalized H.264/AAC streams are sent directly to the RTMPS destination.

### Stop

Press Ctrl+C in the broadcaster terminal. Then verify:

Bash:

```bash
pgrep -x ffmpeg
```

No output means FFmpeg is no longer running. Do not use `kill -9` as the normal stop method; graceful cancellation allows NZYTE TV to clean up its child process and temporary concat file.

After automatic recovery, `pgrep -x ffmpeg` should print a replacement PID. This is distinct from Ctrl+C: parent-broadcaster cancellation is intentional and does not trigger recovery.

## 17. Keep the manual broadcast alive with tmux

tmux keeps a terminal session running when SSH disconnects. NZYTE TV broadcast recovery separately survives supported transient FFmpeg/RTMPS failures. Neither replaces the other. The Checkpoint 1 station supervisor adds manual systemd process supervision, but persistent playback resume after a full restart remains deferred to Checkpoint 2.

Install once if needed:

Bash:

```bash
sudo apt update
sudo apt install -y tmux
```

Start a named session:

Bash:

```bash
tmux new -s nzyte-tv
```

Inside tmux:

1. Use the silent `read -rsp` commands from the secret section.
2. Run `nzytetv broadcast ...`.
3. To detach, press Ctrl+B, release both keys, then press D.

List sessions after reconnecting over SSH:

Bash:

```bash
tmux ls
```

Reconnect:

Bash:

```bash
tmux attach -t nzyte-tv
```

To stop the broadcaster, attach and press Ctrl+C. You may then detach again with Ctrl+B, D. Before closing the session, run `unset NZYTE_TV_RTMP_URL`.

### Checkpoint 1 station supervisor and manual systemd control

For production-foundation testing, install the non-secret `station.json`, root-controlled `secrets.env`, and repository unit by following [Station supervisor and manual systemd operation](station-service.md). Validate before starting:

```bash
/opt/nzyte-tv/app/nzytetv station validate --config /etc/nzyte-tv/station.json
sudo systemctl start nzyte-tv
/opt/nzyte-tv/app/nzytetv station status
journalctl -u nzyte-tv -n 100
journalctl -u nzyte-tv -f
```

Stop cleanly with:

```bash
sudo systemctl stop nzyte-tv
pgrep -x ffmpeg
```

The process check should print nothing. The unit uses `Restart=on-failure`, so successful completion of the fixed queue exits normally and is not replayed automatically.

> **Do not run `systemctl enable nzyte-tv` yet.** Checkpoint 1 does not preserve playback position across a full process restart or Pi reboot. Boot-time enablement waits for Checkpoint 2 acceptance.

## 18. First live-stream acceptance checklist

Confirm all of the following during the first live run:

- [ ] YouTube Studio reports healthy or excellent stream health.
- [ ] Video is visible.
- [ ] Audio is present.
- [ ] Multiple file transitions succeed.
- [ ] The stream does not disconnect at transitions.
- [ ] There is no progressively worsening A/V sync problem.
- [ ] Raspberry Pi CPU remains low because stream-copy is used.

Check FFmpeg without exposing its command line:

Bash:

```bash
pgrep -x ffmpeg
pgrep -x -c ffmpeg
```

## 19. Current operational limitations

The current Checkpoint 1 station foundation:

- uses the supplied playlist queue fixed at broadcaster startup and does not dynamically discover new playlist JSON files;
- does not automatically generate future playlist blocks;
- does not persist runtime playback state across full broadcaster restarts;
- includes a systemd unit for manual start/stop but must not be enabled at boot yet;
- does not resume playback position after a Pi reboot;
- automatically reconnects transient RTMPS/FFmpeg output failures with bounded backoff, restarting the interrupted asset rather than the whole queue;
- does not integrate with the YouTube API; and
- does not monitor YouTube API stream health.

Use tmux for the established manual broadcaster workflow or manually control the Checkpoint 1 station service. Neither is true unattended 24/7 operation. Later checkpoints may add persistent restart state, automatic queue advancement, dynamic future-block generation, and monitoring. The queue remains fixed at process startup.

## 20. What do I do when…?

### I added one new bumper

Copy it to `source/Bumpers`, run `normalize-library`, run the metadata workflow, and rebuild the playlist.

### I created a new bumper while NZYTE TV is live

If the production USB must leave the Pi, wait for a deliberate maintenance point, attach to tmux, stop with Ctrl+C, verify `pgrep -x ffmpeg` prints nothing, unmount the USB, process it on Windows, safely eject it, reconnect and verify the mount, complete metadata, build future playlists with the current history, and restart the broadcaster with the desired queue.

If the content can be prepared on another computer or drive without disturbing active production media, the current broadcast may continue. Add the finished asset for a future playlist later; the active queue does not change automatically.

### I added a new music video

Copy it to `source/Music Videos`, run `normalize-library`, update/verify catalog and metadata, then build a new playlist.

### I changed a source file

Rerun `normalize-library`. The source fingerprint makes the stale output normalize again.

### I renamed a source file intentionally

Use `metadata rebind` where appropriate to preserve `assetId`, then reconcile the normalized filename and run `metadata sync`.

### I pulled new code

Republish the executable. Git changed source only.

### I moved the USB from Windows to Pi

Use `lsblk -f` and `findmnt` to confirm the UUID and stable mount. Do not rely on `E:` or `/dev/sda1` identity.

### I generated another playlist

Use the same current `history.json` so cooldown state crosses the boundary.

### I want to close SSH while streaming

Run the broadcast inside tmux and detach with Ctrl+B, D.

### I rebooted the Pi

The tmux session is gone, and the Checkpoint 1 station unit must not be enabled automatically. Mount validation and manual startup must be repeated. Persistent reboot resume is Checkpoint 2.

## 21. Novice troubleshooting

### Git says the branch already exists

Switch to it; do not try to create it again.

PowerShell:

```powershell
git switch <branch>
git pull --ff-only origin <branch>
```

Bash:

```bash
git switch <branch>
git pull --ff-only origin <branch>
```

Replace `<branch>` with the actual branch name.

### `dotnet` is not found

Install the .NET 10 **SDK**, open a new terminal, and run `dotnet --version`. Do not substitute an older SDK.

### `ffmpeg` or `ffprobe` is not found

Install FFmpeg, open a new terminal, then run `ffmpeg -version` and `ffprobe -version`. Both must be available on `PATH`.

### A multiline command fails immediately

Check the shell. PowerShell uses a backtick (`` ` ``); Bash uses a backslash (`\`). Neither continuation character may have trailing spaces.

### The USB drive letter changed on Windows

Run `Get-Volume`, identify the correct drive, and substitute its actual letter. Do not format it.

### The USB device name changed on the Pi

That is normal. Use `lsblk -f` and mount by UUID. `/srv/nzyte-tv/media` should remain stable.

### The media mount is not active

Run `ls /srv/nzyte-tv/media` to trigger automount, then `findmnt /srv/nzyte-tv/media`. Stop if it points to the wrong filesystem or expected directories are missing.

### Broadcast dry-run reports missing media

Read the reported playlist and sequence. Confirm the playlist's case-sensitive relative path exists under the exact `--library` root. Do not copy an unnormalized source into `library`; rerun normalization and playlist generation as needed.

### Broadcast says `NZYTE_TV_RTMP_URL` is not configured

In the same shell or tmux pane, use the silent `read -rsp` procedure, export the variable, and retry. Do not print the value.

### SSH disconnected but tmux may still be running

Reconnect, run `tmux ls`, then `tmux attach -t nzyte-tv`.

### `tmux` says the session was not found

The session ended or the Pi rebooted. Check `pgrep -x ffmpeg`. If nothing is running, repeat dry-run, secret entry, and broadcast startup in a new tmux session.

### FFmpeg is still running after an expected stop

First reattach to tmux and press Ctrl+C in the broadcaster. Check again with `pgrep -x ffmpeg`. Investigate the owning process before using any manual signal; do not make `kill -9` the normal workflow.

### I confused playlist/history output with dry-run

`build-playlist --dry-run` writes neither playlist nor history. Remove `--dry-run` only when the output paths are correct and you intend to create/update them. `broadcast --dry-run` reads and validates existing playlists but never broadcasts.

## 22. Tested acceptance examples—not permanent requirements

The following have been validated at specific points in production acceptance. They demonstrate that the workflow works; future libraries and queues can have different sizes.

- Windows x64 self-contained publish succeeded.
- Raspberry Pi 4 Linux ARM64 self-contained publish succeeded.
- A portable exFAT USB moved successfully from Windows to the Pi.
- A 232-source normalization batch completed with 0 failures.
- An unchanged integrity pass skipped and verified all 232 assets.
- Metadata initialization resolved the full production inventory without errors.
- Availability-aware playlist generation was validated across multiple deterministic seeds.
- Cross-playlist history behavior was validated.
- A broadcaster dry-run validated 2 playlists, 650 scheduled items, approximately 12 hours, and 0 missing/invalid/unready assets.
- Actual RTMPS YouTube streaming started successfully on the Raspberry Pi.
- Multiple normalized media types streamed through FFmpeg stream-copy.
- tmux successfully kept the manual broadcast session alive across SSH disconnects.
- Historical v0.4.1 readiness acceptance used a fresh self-contained `linux-arm64` candidate from commit `c23e461` (`c23e46168be883e4e857abda5ee23568dc3b7ba0`, "Fix broadcast recovery bypass"), demonstrably different from the prior deployed binary. FFmpeg was deliberately terminated externally twice; NZYTE TV launched a replacement process twice, YouTube resumed after brief loading, and the broadcaster remained stable. The two-playlist queue completed overnight with `Broadcast status: COMPLETED`. Final FFmpeg progress was approximately `out_time=12:00:43`, `bitrate=5833.5 kbits/s`, `speed=1x`, `dup_frames=0`, `drop_frames=0`, `progress=end`. This is historical test evidence—not a guarantee that every future network failure is recoverable.
- During that test, YouTube Studio displayed a low-bitrate advisory around `2812 Kbps`. It was an observed advisory, not a broadcaster failure: playback remained operational, the later/final bitrate was about `5833.5 kbits/s`, and visual inspection on a large television found no obvious quality issue. The existing normalization/broadcast profile remains in use for now; this does not claim it meets every YouTube recommendation. Future profile changes should be evidence-driven by visible artifacts, sustained poor stream health, or another operational reason.
- The published `v0.4.0` tag remains historical at the earlier broadcaster state. The recovery-corrected release is planned as `v0.4.1` after acceptance and documentation are complete; no tag history is changed here.

## 23. Further reading

- [Workstation setup](workstation-setup.md): installation, Git, .NET, and publishing detail.
- [Raspberry Pi setup](raspberry-pi-setup.md): blank-device deployment and Pi-specific background.
- [Media library](media-library.md): storage, normalization, manifests, and portable-drive rules.
- [Content catalog](content-catalog.md): catalog schema, matching, review, edits, and identity.
- [Playlists](playlists.md): scheduling policy, diagnostics, schema, and history.
- [Broadcasting](broadcasting.md): focused broadcaster behavior and validation.
- [Station supervisor](station-service.md): Checkpoint 1 configuration, state, status, secrets, and manual systemd operation.
- [Broadcast standard](broadcast-standard.md): required H.264/AAC technical profile.
