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
optional programming policy
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

The commands have five broad responsibilities:

- **Normalize** is technical media preparation. It converts source masters into the one tested H.264/AAC broadcast format and verifies the result.
- **Metadata and catalog** describe content identity and programming information. They answer “what asset is this?” and “which song does it represent?”
- **Programming policy** optionally supplies sparse editorial controls, one spotlight campaign, and internal pacing patterns without duplicating content identity.
- **Build playlist** is television programming. It creates a deterministic, airtime-aware schedule and preserves planned cooldown state in history.
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
| `programming init` | Creates the optional versioned policy without replacing an existing one. | Explicitly activate Checkpoint 3A on a media root. |
| `programming validate` | Validates policy values plus referenced catalog groups and library asset IDs. | Before playlist generation and after policy edits. |
| `programming status` | Summarizes campaign, overrides, repetition, cadence, and internal patterns. | Inspect the active finite-block policy. |
| `programming campaign` / `programming asset` | Manages the spotlight record and sparse per-asset editorial overrides. | Routine programming changes without hand-editing JSON. |
| `programming rolling init` | Creates one portable rolling-planner lineage with an explicit empty or imported planned-history genesis. | Once, before preparing the 3B1 buffer. |
| `programming rolling maintain` | Reconciles interrupted work and ensures the manifest's three-block minimum. It never shrinks an automatically extended manifest or starts FFmpeg. | Prepare, repair, or verify the rolling buffer. |
| `programming rolling validate` / `status` | Validates hashes, chains, media readiness, and broadcast plans; reports buffer/audit state. | After maintenance and before later acceptance work. |
| `build-playlist` | Creates a deterministic airtime-aware schedule and optionally updates history. | After the eligible library changes or another programming block is needed. |
| `broadcast` | Validates playlist files, concatenates normalized assets, and stream-copies them to RTMP/RTMPS using FFmpeg. | Dry-run first; then start the live stream. |
| `station validate` | Validates non-secret station configuration and the existing broadcast readiness checks without launching FFmpeg. | Before every station/service start or after configuration changes. |
| `station run` | Supervises the configured fixed queue through the existing resilient broadcaster and writes durable runtime state/heartbeat. | Checkpoint 2 item-level restart/reboot resume. |
| `station status` | Reads runtime state, resume telemetry, heartbeat freshness, and station PID without displaying process arguments. | Check station, broadcast, FFmpeg, media, queue, and persistence state. |
| `station rolling validate` / `status` | Read-only execution and replenishment status across the rolling manifest, block execution state, CP2 state, and advisory health. | Before an opt-in rolling test and while diagnosing it. |
| `station rolling run` | Claims one immutable block at a time, delegates it to the unchanged station supervisor, and asynchronously maintains two committed future blocks through the accepted planner. | Checkpoint 3B2-A/B development/acceptance only; no production service is installed yet. |

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
4. **Generating future playlists:** this may be done while an existing queue runs. Manual blocks must share the correct ordinary history; 3B1 rolling blocks use their own manifest-committed content-addressed planned history. Either way, the new JSON is only a future plan.
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

Do not use ordinary `history.json` as the transaction authority for a rolling lineage. `programming rolling maintain` advances that lineage's `historyHead` only when the same atomic manifest commit adds its immutable block. Prepared blocks are ignored by ordinary `broadcast` and static `station run`; only an explicitly configured `station rolling run` may claim them.

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

### 9.3 Initialize and manage optional programming policy

Checkpoint 3A policy is opt-in. Existing roots without `catalog/programming.json` continue using the accepted legacy scheduler. To activate the new policy on the portable drive, initialize it once, then validate and inspect it:

PowerShell:

```powershell
Set-Location C:\Tools\NzyteTv\candidate
.\nzytetv.exe programming init --media-root "E:\"
.\nzytetv.exe programming validate --media-root "E:\"
.\nzytetv.exe programming status --media-root "E:\"
```

`programming init` is idempotent and never overwrites an existing policy. The portable file is `E:\catalog\programming.json`. It contains no destination secret or station runtime state.

Common editorial changes are:

```powershell
.\nzytetv.exe programming campaign set free-fallin --media-root "E:\"
.\nzytetv.exe programming campaign clear --media-root "E:\"
.\nzytetv.exe programming asset set free-fallin-video --media-root "E:\" --do-not-air true
.\nzytetv.exe programming asset set free-fallin-visualizer --media-root "E:\" --weight 1.5
.\nzytetv.exe programming asset reset free-fallin-video --media-root "E:\"
```

Use real catalog `contentGroupId` and sidecar `assetId` values. A prepared asset needs no policy entry to air: technical/metadata eligibility remains authoritative, and only an explicit Do Not Air override excludes it. See [V1 programming policy](programming.md) for validation, campaign, repetition, pattern, bumper, and promo details.

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

The seed is any chosen 32-bit integer. `build-playlist` automatically uses `programming.json` beside the supplied catalog when it exists. Reusing the same eligible library, catalog, programming policy, history input, generation time, target duration, and seed makes the schedule reproducible.

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

### 10.3 Prepare the Checkpoint 3B1 rolling buffer

The rolling planner is optional and does not replace the manual commands above. Initialize it once, explicitly choosing either an imported planned history or empty genesis. This example chooses empty genesis and a fixed base seed:

```bash
/opt/nzyte-tv/app/nzytetv programming rolling init \
  --media-root /srv/nzyte-tv/media \
  --base-seed 20261001

/opt/nzyte-tv/app/nzytetv programming rolling maintain \
  --media-root /srv/nzyte-tv/media

/opt/nzyte-tv/app/nzytetv programming rolling validate \
  --media-root /srv/nzyte-tv/media

/opt/nzyte-tv/app/nzytetv programming rolling status \
  --media-root /srv/nzyte-tv/media
```

Omit `--history` only when an empty planned-history genesis is intentional. To import, add `--history <existing-planned-history.json>` to `rolling init`; the planner never searches for one automatically. The first maintenance run commits an activation candidate plus two future blocks under `playlists/rolling`. Repeating it at or below the committed count still reconciles interrupted work and otherwise changes no committed artifact. If automatic replenishment later extends the manifest beyond three blocks, ordinary `programming rolling maintain` remains a successful no-op after reconciliation; it never truncates the ledger. New eligible content and policy changes affect the next block not yet generated and never rewrite committed blocks.

Production initialization always defaults to six hours (21,600 seconds). For accelerated handoff acceptance only, a completely separate test media root may be initialized with `--test-block-duration 4m`; accepted values are 60–1,800 whole seconds, and the resulting output is prominently labeled `TEST LINEAGE — NON-PRODUCTION DURATION`. Never apply the option to the production media root or reuse production station/runtime state. It is initialization-only, does not trim assets, and cannot change an existing lineage to a different duration. Follow [Accelerated rolling integration testing](accelerated-rolling-testing.md) for the isolation checklist.

Checkpoint 3B1 commands do not put those blocks on air, change the static `station.json` queue, start FFmpeg, or hand off at a block boundary. The separate opt-in rolling command performs execution and automatic future-buffer maintenance, but it has not changed the accepted static service or completed production Pi/YouTube boundary acceptance. See [Rolling programming planner](rolling-programming.md) for the transaction model.

### 10.3.1 Validate the opt-in rolling coordinator and replenisher

Create `/etc/nzyte-tv/rolling-station.json` from `deploy/config/rolling-station.json.example`. Copy the exact planner lineage printed by `programming rolling status`; do not invent or shorten it. The configuration references the existing static `station.json` and uses a distinct rolling state path such as `/var/lib/nzyte-tv/rolling-state.json`.

Read-only checks:

```bash
/opt/nzyte-tv/app/nzytetv station rolling validate \
  --config /etc/nzyte-tv/rolling-station.json

/opt/nzyte-tv/app/nzytetv station rolling status \
  --config /etc/nzyte-tv/rolling-station.json
```

The planner manifest remains authoritative for committed order. CP2 `state.json` remains authoritative for the item cursor. The separate rolling document records only active/completed block identity. The optional `rolling-state.json.replenishment.json` sidecar is advisory and contains buffer/retry health, never execution authority. Validation and status do not mutate any document or invoke generation.

Do not run the static station and rolling coordinator together. Checkpoints 3B2-A/B supply no systemd unit. For a later manual acceptance run, stop the static service first and run `station rolling run` from a controlled terminal. Only the process that owns the coordinator lock starts automatic replenishment. An unfinished `STOPPED` static queue is refused unless the operator deliberately supplies `--accept-stopped-static-cutover` on the first run; that option cannot override a live, interrupted, schema-v1, invalid, or already-rolling state.

Status calculates buffer truth from the authoritative manifest and rolling execution state, rather than trusting the sidecar. With the current three-block window it reports two future blocks, the active/next-required anchor, highest committed sequence, required highest sequence, deficit, buffer health, replenishment health, last successful replenishment, and the last redacted error. `HEALTHY` means the full relative window exists; `LOW` means one future block remains; `EMPTY` means no future block remains beyond the anchor. `BLOCKED` replenishment can coexist with executable committed blocks.

If the exact next block is missing, status becomes `WAITINGFORBLOCK`; the coordinator polls with bounded 5/15/30/60-second delays and never replays the prior block. The background replenisher independently retries transient planner failures with the same 5/15/30/60-second progression and lets the coordinator observe a later atomic manifest publication. Invalid future policy or insufficient inventory blocks new generation without making already-committed blocks unplayable. See [Rolling station coordinator](rolling-station.md) for claim, completion-sealing, crash reconciliation, replenishment, security, and the production-acceptance boundary.

### 10.4 Configured and effective airtime targets

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

### 10.5 Song pacing in plain language

With Checkpoint 3A policy active, the exact same `assetId` keeps the configurable two-hour preferred cooldown. Song-family pacing uses substantial pieces instead of a long time ban: the same `contentGroupId` cannot occupy adjacent substantial slots while any alternative exists, and it is strongly avoided within the previous two substantial pieces by default. Bumper, promo, interstitial, and advertisement inserts do not count as song separation. A different presentation can return after the configured lookback when the schedule permits.

Short-form and song-based music/performance/animated presentations at or below the existing 60-second threshold count toward `maximumConsecutiveShortPieces`, which defaults to three. A full song, vlog, or special resets the run. Bumper, promo, interstitial, and advertisement inserts do not reset it. If a fourth short is unavoidable because no valid non-short substantial candidate survives the normal rules, generation continues and reports a `shortRunRelaxations` diagnostic; otherwise a non-short piece is selected.

The following time-based same-song rules describe legacy compatibility when `programming.json` is absent.

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

### 10.6 Cadence content is not filler

- Promo: blocked below 30 minutes, preferred at 30–45 minutes, overdue after 45 minutes. Under Checkpoint 3A, `promo` and `advertisement` share this mini-break timer.
- Interstitial: blocked below 20 minutes, preferred at 20—30 minutes, overdue after 30 minutes.
- Bumper: legacy scheduling requires at least four normal programs. Checkpoint 3A defaults to a deterministic 3–5 substantial-piece window and rotates eligible station IDs before repeating when inventory permits.

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

Environment variables are safer than command arguments. The station systemd workflow uses the root-controlled `/etc/nzyte-tv/secrets.env` described in the [station service guide](station-service.md). When finished with a manual shell session:

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

tmux keeps a terminal session running when SSH disconnects. NZYTE TV broadcast recovery separately survives supported transient FFmpeg/RTMPS failures. Neither replaces the other. The Checkpoint 2 station supervisor adds durable item-level resume across a full station-process restart; systemd provides process/boot supervision. These remain separate responsibilities.

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

### Accepted Checkpoint 2 station resume and systemd control

Checkpoint 2 passed Raspberry Pi acceptance for hard parent failure, clean stop/start, graceful reboot, and boot-enabled reboot. Install the non-secret `station.json`, root-controlled `secrets.env`, and repository unit by following [Station supervisor, persistent resume, and systemd operation](station-service.md). Validate before starting:

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

The process check should print nothing. Clean stop writes `STOPPED` while preserving queue identity and the resume cursor, so starting the same queue again restarts the interrupted item from its beginning instead of sequence 1. A normal reboot uses the same graceful SIGTERM path.

The unit uses `Restart=on-failure`, so an unexpected parent-process failure is eligible for restart and matching schema-v2 state resumes. Successful fixed-queue completion exits zero and is not replayed. Permanent startup/configuration and resume-safety failures exit 78; `RestartPreventExitStatus=78` prevents a systemd restart loop.

Boot enablement is still an explicit operator action; NZYTE TV code and installation steps never enable the unit automatically. The accepted production Pi completed the prerequisite tests before the operator enabled it.

That unit still runs the accepted static `station run` command. Checkpoints 3B2-A/B do not modify, replace, install, or enable a systemd unit. Use the separate rolling command only during its explicit acceptance workflow.

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

The accepted Checkpoint 2 station supervisor, unchanged by Checkpoints 3A, 3B1, 3B2-A, and 3B2-B:

- uses the supplied playlist queue fixed at broadcaster startup and does not dynamically discover new playlist JSON files;
- does not itself automatically generate future playlist blocks (the separate rolling runtime host may ask the accepted planner to do so);
- persists item-level runtime progress for the fixed configured queue across full station-process restarts;
- restarts the saved item from its beginning and never attempts timestamp/frame resume;
- preserves the resume cursor on clean stop so a normal graceful reboot can resume;
- includes a boot-suitable systemd unit; enablement is an explicit operator choice, and enabled reboot resume has passed Pi acceptance;
- automatically reconnects transient RTMPS/FFmpeg output failures with bounded backoff, restarting the interrupted asset rather than the whole queue;
- does not integrate with the YouTube API; and
- does not monitor YouTube API stream health.

Each execution queue remains immutable and fixed at startup. The opt-in coordinator may start the next separately committed six-hour block only after positive completion of the current block; it never appends to an active queue. The 3B2-B runtime host asynchronously maintains two future immutable blocks through the accepted planner while preserving planned-history transactions. It does not preserve one FFmpeg ingest connection across boundaries, install a rolling systemd service, call the YouTube API, monitor remote stream health, or alert an operator. It is not yet accepted unattended 24/7 production operation.

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

For a 3B1 rolling lineage, do not edit or copy its history files manually. Run `programming rolling maintain`; only `manifest.json` commits a block and advances its content-addressed planned-history head.

### I want to close SSH while streaming

Run the broadcast inside tmux and detach with Ctrl+B, D.

### I rebooted the Pi

The tmux session is gone. systemd's graceful SIGTERM writes `STOPPED` with the durable cursor. If the operator has enabled the accepted station unit, it should start at boot and resume the same queue item from its beginning; otherwise start it manually with `sudo systemctl start nzyte-tv`. NZYTE TV never enables the unit itself.

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
- Checkpoint 2 persistent resume passed hard parent-process failure, clean stop/start, graceful reboot while disabled, and final boot-enabled reboot acceptance on Raspberry Pi, retaining the item-level cursor with exactly one FFmpeg process.
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
- [V1 programming policy](programming.md): editorial controls, spotlight campaign, song-family pacing, internal patterns, and cadence.
- [Rolling programming planner](rolling-programming.md): immutable blocks, manifest transactions, deterministic retry, recovery, and Pi acceptance.
- [Rolling station coordinator](rolling-station.md): immutable claims, CP2 reconciliation, completion sealing, block handoff, automatic future-buffer replenishment, and the 3B2-C acceptance boundary.
- [Broadcasting](broadcasting.md): focused broadcaster behavior and validation.
- [Station supervisor](station-service.md): accepted Checkpoint 2 queue identity, durable resume, state/status, secrets, systemd behavior, and Pi acceptance evidence.
- [Broadcast standard](broadcast-standard.md): required H.264/AAC technical profile.
