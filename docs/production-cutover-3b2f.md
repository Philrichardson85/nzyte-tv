# Checkpoint 3B2-F production cutover preparation

This runbook prepares the accepted rolling architecture for a future `v0.5.0` production cutover. It is not authorization to deploy, stop the accepted test station, merge, push, or tag. Commands under **Controlled cutover** and **Rollback** change Raspberry Pi services and must run only after explicit approval.

## Compatibility findings and migration decision

The production planner is already initialized and owns three immutable six-hour blocks under `/srv/nzyte-tv/media/playlists/rolling`. Preserve that lineage. Do **not** run `programming rolling init`, do not replace `manifest.json`, and do not copy any planner, CP2, rolling, or replenishment state from `/opt/nzyte-tv/integration/3b2d`.

The existing `/etc/nzyte-tv/station.json` with two configured playlists is compatible with rolling execution and remains the production static configuration referenced by the new rolling configuration. The loader still validates both static playlist paths, `mediaRoot`, `libraryRoot`, and `/var/lib/nzyte-tv/state.json`. For execution, `RollingStationCoordinator` replaces only `Playlists` in an in-memory configuration with the one immutable claimed block. No generated replacement `station.json` is required.

Production needs one new non-secret `/etc/nzyte-tv/rolling-station.json`, containing the exact existing production `plannerId` and a new `/var/lib/nzyte-tv/rolling-state.json` path. The runtime state file itself must be absent before first rolling execution; the coordinator creates it. Its replenishment sidecar must also be absent. The existing CP2 `/var/lib/nzyte-tv/state.json` is preserved and backed up before authorization to start rolling.

Initial CP2 state determines the supported transition:

- `COMPLETED`, schema 2, dead recorded process: first rolling claim needs no override.
- `STOPPED`, schema 2, dead recorded process: the operator must create the one-time authorization marker documented below. The launcher supplies `--accept-stopped-static-cutover` only while rolling state is absent.
- schema 1, a live recorded process, or a dead `STARTING`, `BROADCASTING`, `STOPPING`, or `FAILED` state: stop. Do not edit or delete JSON. Resolve that state through a separately reviewed static-station recovery before production cutover.
- any existing production `rolling-state.json`: stop. It is unexpected under the stated baseline and requires review rather than replacement.

The application does not migrate planner artifacts or rewrite CP2 state during preparation. Once production starts, normal CP2 writes describe the claimed rolling block. The backup makes the authorized runtime transition reversible.

## Production layout

```text
/opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/       staged ARM64 application
/opt/nzyte-tv/app                                    production application path
/opt/nzyte-tv/production/                            root-owned launch/handoff scripts
/opt/nzyte-tv/production/recovery/                   accepted parameterized E3 engine
/etc/nzyte-tv/station.json                           unchanged static roots/state configuration
/etc/nzyte-tv/rolling-station.json                   new production rolling configuration
/var/lib/nzyte-tv/state.json                         existing CP2 authority
/var/lib/nzyte-tv/rolling-state.json                 new rolling authority, runtime-created
/var/lib/nzyte-tv/rolling-state.json.replenishment.json  advisory runtime-created sidecar
/var/lib/nzyte-tv/broadcast-diagnostics.json         advisory E1 diagnostics
/var/lib/nzyte-tv/recovery/                           production boot-recovery state
```

The production recovery state, E1 diagnostics, and replenishment sidecar are advisory or operational files. They are not planner or execution authority.

## Preparation

### 1. Verify source and build a versioned candidate

Before replacing the current production checkout, record and require its stated baseline:

```bash
cd /opt/nzyte-tv/src
git status --short
test "$(git rev-parse HEAD)" = \
  "0e48fdc475fa7f1bf49a95dd3c31e82d574c3f8b"
```

`git status --short` must print nothing. Make the reviewed 3B2-F commit available only through the separately approved source-transfer or push workflow, then detach the Pi checkout at the exact commit reported for this checkpoint.

Before publishing, compare `HEAD` to that exact reviewed commit. These additional checks prove that it descends from accepted E3 and retains the accepted application source tree:

```bash
cd /opt/nzyte-tv/src
git status --short
git rev-parse HEAD
git merge-base --is-ancestor \
  2930551e07823d9e7c1230f9e594e34588218651 HEAD
test "$(git rev-parse HEAD:src)" = \
  "ae911753fc306969987b4ef264f9edf9cffde309"
test "$(uname -m)" = "aarch64"

dotnet restore NzyteTv.slnx
dotnet build NzyteTv.slnx --configuration Release --no-restore
dotnet test NzyteTv.slnx --configuration Release --no-build --no-restore
```

`git status --short` must still print nothing. Stop on any failed command.

Build outside the live application path:

```bash
sudo test ! -e /opt/nzyte-tv/releases/v0.5.0-3b2f-candidate
sudo install -d -o u24 -g u24 -m 0755 /opt/nzyte-tv/releases
sudo install -d -o u24 -g u24 -m 0755 \
  /opt/nzyte-tv/releases/v0.5.0-3b2f-candidate

cd /opt/nzyte-tv/src
dotnet publish src/NzyteTv.Cli/NzyteTv.Cli.csproj \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained true \
  --no-restore \
  --output /opt/nzyte-tv/releases/v0.5.0-3b2f-candidate

file /opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv
/opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv --help
```

`file` must report an ARM aarch64 executable. The existing `/opt/nzyte-tv/app` is unchanged during this stage.

### 2. Verify the production baseline and create a reversible backup

These are the accepted pre-checkpoint hashes:

```bash
printf '%s  %s\n' \
  '27bdb55cba7b125866f727625ee72eac36c136106edbc8a1b9218c5f28f20d93' \
  '/srv/nzyte-tv/media/playlists/rolling/manifest.json' \
  | sha256sum --check --strict
printf '%s  %s\n' \
  '638a6996d9cb4d5cbf2f473266f5bc2432f52d36a45bfb8ea868c987512b44eb' \
  '/var/lib/nzyte-tv/state.json' \
  | sudo sha256sum --check --strict
```

Stop if either hash differs. Do not “repair” the file.

Require the stated service isolation and absence of prior production rolling execution:

```bash
systemctl is-enabled nzyte-tv.service
systemctl is-active nzyte-tv.service
systemctl is-active nzyte-tv-3b2d.service
systemctl is-enabled nzyte-tv-3b2e-boot-reconnect.timer

sudo test ! -e /var/lib/nzyte-tv/rolling-state.json
sudo test ! -e /var/lib/nzyte-tv/rolling-state.json.replenishment.json
sudo test ! -e /var/lib/nzyte-tv/accept-stopped-static-cutover
```

Expected results are production `disabled`/`inactive`, test `active`, test recovery `enabled`, and all three file-absence checks successful.

Create a timestamped backup without modifying the originals:

```bash
CUTOVER_UTC="$(date -u +%Y%m%dT%H%M%SZ)"
CUTOVER_BACKUP="/var/backups/nzyte-tv/3b2f-${CUTOVER_UTC}"
sudo install -d -o root -g root -m 0700 "$CUTOVER_BACKUP"

sudo cp --archive /var/lib/nzyte-tv/state.json \
  "$CUTOVER_BACKUP/state.json"
sudo cp --archive /etc/nzyte-tv/station.json \
  "$CUTOVER_BACKUP/station.json"
sudo cp --archive /etc/systemd/system/nzyte-tv.service \
  "$CUTOVER_BACKUP/nzyte-tv.service"
sudo tar --create --file "$CUTOVER_BACKUP/rolling-programming.tar" \
  --directory /srv/nzyte-tv/media playlists/rolling

printf '%s\n' "$CUTOVER_BACKUP" \
  | sudo tee /var/backups/nzyte-tv/3b2f-current-backup.txt >/dev/null
sudo sha256sum \
  "$CUTOVER_BACKUP/state.json" \
  "$CUTOVER_BACKUP/station.json" \
  "$CUTOVER_BACKUP/nzyte-tv.service" \
  "$CUTOVER_BACKUP/rolling-programming.tar" \
  | sudo tee "$CUTOVER_BACKUP/SHA256SUMS" >/dev/null
```

The rolling archive is evidence and an emergency recovery source. Never restore it automatically over a lineage that may have legitimately appended later blocks.

### 3. Create the production rolling configuration

Read the lineage ID directly from the existing manifest and validate its form:

```bash
PRODUCTION_MANIFEST=/srv/nzyte-tv/media/playlists/rolling/manifest.json
PRODUCTION_PLANNER_ID="$(python3 -c \
  'import json,re,sys; d=json.load(open(sys.argv[1], encoding="utf-8")); p=d.get("plannerId"); assert d.get("schemaVersion")==1 and isinstance(p,str) and re.fullmatch(r"[0-9a-fA-F]{32}",p); print(p.lower())' \
  "$PRODUCTION_MANIFEST")"
printf 'Production planner ID: %s\n' "$PRODUCTION_PLANNER_ID"

ROLLING_CONFIG_CANDIDATE="$(mktemp)"
sed "s/REPLACE_WITH_PRODUCTION_PLANNER_ID/${PRODUCTION_PLANNER_ID}/" \
  /opt/nzyte-tv/src/deploy/checkpoint-3b2-f/rolling-station.json.example \
  > "$ROLLING_CONFIG_CANDIDATE"
sudo install -o root -g u24 -m 0640 \
  "$ROLLING_CONFIG_CANDIDATE" /etc/nzyte-tv/rolling-station.json
rm -f -- "$ROLLING_CONFIG_CANDIDATE"
```

Do not edit `/etc/nzyte-tv/station.json`. Validate the existing lineage and the candidate application read-only:

```bash
/opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv \
  programming rolling validate --media-root /srv/nzyte-tv/media
/opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv \
  programming rolling status --media-root /srv/nzyte-tv/media
/opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv \
  station rolling validate --config /etc/nzyte-tv/rolling-station.json
/opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv \
  station rolling status --config /etc/nzyte-tv/rolling-station.json
/opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv \
  station status --state /var/lib/nzyte-tv/state.json
```

Require a production-duration lineage, exactly the expected planner ID, three valid committed blocks, valid static configuration/playlists, and no unexpected rolling execution state. The destination may report `NOT CONFIGURED` in this non-systemd shell; do not load or print the secrets file merely to change that line.

Record whether CP2 is `COMPLETED` or `STOPPED`. For `STOPPED` only, stage explicit one-time authorization:

```bash
sudo install -o root -g u24 -m 0440 /dev/null \
  /var/lib/nzyte-tv/accept-stopped-static-cutover
```

Do not create that marker for `COMPLETED`. Stop for review for every other phase.

### 4. Stage scripts, units, diagnostics, and boot protection

Install root-controlled scripts without changing a running service:

```bash
cd /opt/nzyte-tv/src
sudo install -d -o root -g root -m 0755 /opt/nzyte-tv/production
sudo install -d -o root -g root -m 0755 /opt/nzyte-tv/production/recovery
sudo install -d -o root -g root -m 0755 /etc/nzyte-tv/3b2f-candidate-units
sudo install -d -o root -g root -m 0700 /var/lib/nzyte-tv/recovery

sudo install -o root -g root -m 0755 \
  deploy/checkpoint-3b2-f/nzyte-tv-production-launch.sh \
  /opt/nzyte-tv/production/nzyte-tv-production-launch.sh
sudo install -o root -g root -m 0755 \
  deploy/checkpoint-3b2-f/nzyte-tv-production-handoff.sh \
  /opt/nzyte-tv/production/nzyte-tv-production-handoff.sh
sudo install -o root -g root -m 0755 \
  deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.sh \
  /opt/nzyte-tv/production/recovery/nzyte-tv-boot-reconnect.sh
sudo install -o root -g root -m 0755 \
  deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-read-diagnostics.py \
  /opt/nzyte-tv/production/recovery/nzyte-tv-read-diagnostics.py

sudo install -o root -g root -m 0644 \
  deploy/checkpoint-3b2-f/nzyte-tv.service \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv.service
sudo install -o root -g root -m 0644 \
  deploy/checkpoint-3b2-f/nzyte-tv-boot-reconnect.service \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv-boot-reconnect.service
sudo install -o root -g root -m 0644 \
  deploy/checkpoint-3b2-f/nzyte-tv-boot-reconnect.timer \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv-boot-reconnect.timer

sudo systemd-analyze verify \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv.service \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv-boot-reconnect.service \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv-boot-reconnect.timer
```

The production unit deliberately matches the accepted test service's graceful-stop contract: `KillSignal=SIGINT`, `KillMode=mixed`, and `TimeoutStopSec=90`. The application converts `SIGINT` into cancellation, stops its FFmpeg child, and persists the CP2 and rolling stopped states. `mixed` lets the application perform that coordinated shutdown before systemd applies a final cgroup kill if the 90-second bound expires.

Seed production recovery with the current boot ID before any production timer can be enabled:

```bash
sudo env E3_STATE_DIR=/var/lib/nzyte-tv/recovery \
  /opt/nzyte-tv/production/recovery/nzyte-tv-boot-reconnect.sh seed
sudo cmp -s /proc/sys/kernel/random/boot_id \
  /var/lib/nzyte-tv/recovery/last-attempt-boot-id
sudo cat /var/lib/nzyte-tv/recovery/last-outcome.env
```

Require `result=seeded` and `reason=current-boot-protected`. No service action occurs during seeding.

## Preflight immediately before controlled cutover

Do not continue unless all checks pass:

```bash
systemctl is-enabled nzyte-tv.service
systemctl is-active nzyte-tv.service
systemctl is-enabled nzyte-tv-boot-reconnect.timer 2>/dev/null || true
systemctl is-active nzyte-tv-3b2d.service
systemctl is-enabled nzyte-tv-3b2e-boot-reconnect.timer
pgrep -x -c nzytetv
pgrep -x -c ffmpeg

sudo sha256sum --check "$(sudo cat /var/backups/nzyte-tv/3b2f-current-backup.txt)/SHA256SUMS"
printf '%s  %s\n' \
  '27bdb55cba7b125866f727625ee72eac36c136106edbc8a1b9218c5f28f20d93' \
  '/srv/nzyte-tv/media/playlists/rolling/manifest.json' \
  | sha256sum --check --strict
printf '%s  %s\n' \
  '638a6996d9cb4d5cbf2f473266f5bc2432f52d36a45bfb8ea868c987512b44eb' \
  '/var/lib/nzyte-tv/state.json' \
  | sudo sha256sum --check --strict

sudo test ! -e /var/lib/nzyte-tv/rolling-state.json
sudo test ! -e /var/lib/nzyte-tv/rolling-state.json.replenishment.json
```

Expected service/count results remain production disabled/inactive, production recovery absent or disabled, test active, test recovery enabled, and counts `1`/`1`. Record safe `systemctl show` properties, the hashes, planner status, rolling validation, CP2 status, and PID counts in the timestamped evidence directory. Do not capture environment contents, process command lines, raw FFmpeg stderr, or unfiltered journals.

## Controlled cutover

The following commands are the first service-changing operations.

Install the candidate units while production remains disabled:

```bash
sudo install -o root -g root -m 0644 \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv.service \
  /etc/systemd/system/nzyte-tv.service
sudo install -o root -g root -m 0644 \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv-boot-reconnect.service \
  /etc/systemd/system/nzyte-tv-boot-reconnect.service
sudo install -o root -g root -m 0644 \
  /etc/nzyte-tv/3b2f-candidate-units/nzyte-tv-boot-reconnect.timer \
  /etc/systemd/system/nzyte-tv-boot-reconnect.timer
sudo systemctl daemon-reload
systemctl is-enabled nzyte-tv.service
systemctl is-enabled nzyte-tv-boot-reconnect.timer
systemctl show nzyte-tv.service \
  --property=KillSignal \
  --property=KillMode \
  --property=TimeoutStopUSec
```

Require the shutdown properties to resolve to `SIGINT` (signal 2), `mixed`, and 90 seconds before continuing.

Both must still report `disabled`.

Promote the versioned application while production is inactive. These commands require the original `app` to be a directory and the rollback target to be absent:

```bash
sudo test -d /opt/nzyte-tv/app
sudo test ! -L /opt/nzyte-tv/app
sudo test ! -e /opt/nzyte-tv/releases/pre-v0.5.0-0e48fdc
sudo test -x /opt/nzyte-tv/releases/v0.5.0-3b2f-candidate/nzytetv

sudo mv /opt/nzyte-tv/app \
  /opt/nzyte-tv/releases/pre-v0.5.0-0e48fdc
sudo ln -s releases/v0.5.0-3b2f-candidate /opt/nzyte-tv/app
/opt/nzyte-tv/app/nzytetv --help
```

Perform the exclusive handoff:

```bash
sudo /opt/nzyte-tv/production/nzyte-tv-production-handoff.sh cutover
```

The helper disables the test E3 timer, requires that its recovery oneshot is not already running, stops and disables the test service, verifies both processes are gone, then enables and starts production. It verifies exactly one `nzytetv`, exactly one FFmpeg, systemd MainPID ownership, and FFmpeg parent ownership. Production recovery deliberately remains disabled. A production start failure disables production and leaves both broadcasters stopped; it never restarts the test automatically.

If the helper stops before the test service is disabled—for example because a recovery oneshot is already active—the test broadcast may remain active while its timer is disabled. Verify production is still inactive and wait for the oneshot to finish; then either retry `cutover` or re-enable only the test timer. If the test broadcaster has already stopped, follow rollback instead. Never start either service merely from an assumed failure stage.

## Verification and production recovery enablement

Verify local execution without exposing arguments or credentials:

```bash
systemctl is-enabled nzyte-tv-3b2d.service
systemctl is-active nzyte-tv-3b2d.service
systemctl is-enabled nzyte-tv-3b2e-boot-reconnect.timer
systemctl is-enabled nzyte-tv.service
systemctl is-active nzyte-tv.service
systemctl is-enabled nzyte-tv-boot-reconnect.timer
pgrep -x -c nzytetv
pgrep -x -c ffmpeg

/opt/nzyte-tv/app/nzytetv station rolling status \
  --config /etc/nzyte-tv/rolling-station.json
/opt/nzyte-tv/production/recovery/nzyte-tv-read-diagnostics.py \
  /var/lib/nzyte-tv/broadcast-diagnostics.json
```

Require test disabled/inactive, test recovery disabled, production enabled/active, production recovery still disabled, exactly one application and FFmpeg, a valid active rolling claim, two committed future blocks, and advancing safe diagnostics. The production recovery profile also requires production to remain enabled before it will act. Manually verify moving YouTube video and audible audio.

Once `/var/lib/nzyte-tv/rolling-state.json` exists and status agrees with the production lineage, remove the one-time marker if one was used:

```bash
sudo rm -f -- /var/lib/nzyte-tv/accept-stopped-static-cutover
```

Seed immediately again and enable production recovery without interrupting the healthy stream:

```bash
sudo env E3_STATE_DIR=/var/lib/nzyte-tv/recovery \
  /opt/nzyte-tv/production/recovery/nzyte-tv-boot-reconnect.sh seed
sudo systemctl enable --now nzyte-tv-boot-reconnect.timer

systemctl is-enabled nzyte-tv-boot-reconnect.timer
systemctl list-timers --all nzyte-tv-boot-reconnect.timer --no-pager
sudo cat /var/lib/nzyte-tv/recovery/last-outcome.env
```

The outcome must remain `seeded`; enabling the timer on this boot must not restart production.

For reboot acceptance, capture the boot ID, safe systemd properties, rolling status, checksums, PID counts, and allowlisted E1 reader output before reboot. After at least 220 seconds, require one production recovery outcome for the new boot, production active, test disabled, one owned application/FFmpeg pair, advancing rolling execution, intact committed blocks, two future blocks, and viewer-confirmed video/audio. Repeat on a second later reboot. Do not accept merely because the timer fired or ingest is healthy.

## Rollback

Rollback is deliberately split so no test process starts until production recovery is disabled and inactive and the production broadcaster is stopped and verified gone.

### 1. Stop production and its recovery

```bash
sudo /opt/nzyte-tv/production/nzyte-tv-production-handoff.sh \
  rollback-stop-production
pgrep -x -c nzytetv
pgrep -x -c ffmpeg
```

Both counts must be `0`. Do not continue otherwise.

### 2. Preserve failed evidence and restore production files/state

```bash
CUTOVER_BACKUP="$(sudo cat /var/backups/nzyte-tv/3b2f-current-backup.txt)"
ROLLBACK_UTC="$(date -u +%Y%m%dT%H%M%SZ)"
sudo install -d -o root -g root -m 0700 \
  "$CUTOVER_BACKUP/failed-${ROLLBACK_UTC}"

sudo test -L /opt/nzyte-tv/app
sudo mv /opt/nzyte-tv/app \
  "$CUTOVER_BACKUP/failed-${ROLLBACK_UTC}/app-candidate-link"
sudo mv /opt/nzyte-tv/releases/pre-v0.5.0-0e48fdc \
  /opt/nzyte-tv/app

sudo cp --archive /var/lib/nzyte-tv/rolling-state.json \
  "$CUTOVER_BACKUP/failed-${ROLLBACK_UTC}/" 2>/dev/null || true
sudo cp --archive /var/lib/nzyte-tv/rolling-state.json.replenishment.json \
  "$CUTOVER_BACKUP/failed-${ROLLBACK_UTC}/" 2>/dev/null || true
sudo rm -f -- /var/lib/nzyte-tv/rolling-state.json
sudo rm -f -- /var/lib/nzyte-tv/rolling-state.json.replenishment.json
sudo install -o u24 -g u24 -m 0640 \
  "$CUTOVER_BACKUP/state.json" /var/lib/nzyte-tv/state.json
sudo rm -f -- /var/lib/nzyte-tv/accept-stopped-static-cutover

sudo install -o root -g root -m 0644 \
  "$CUTOVER_BACKUP/nzyte-tv.service" \
  /etc/systemd/system/nzyte-tv.service
sudo rm -f -- /etc/systemd/system/nzyte-tv-boot-reconnect.timer
sudo rm -f -- /etc/systemd/system/nzyte-tv-boot-reconnect.service
sudo systemctl daemon-reload

/opt/nzyte-tv/app/nzytetv --help
printf '%s  %s\n' \
  '638a6996d9cb4d5cbf2f473266f5bc2432f52d36a45bfb8ea868c987512b44eb' \
  '/var/lib/nzyte-tv/state.json' \
  | sudo sha256sum --check --strict
```

This is the explicitly authorized restoration of the backed-up CP2 state. The production rolling configuration may remain for diagnosis because the restored static unit does not reference it.

Do not automatically restore `rolling-programming.tar`. First compare the live manifest and archive. A healthy lineage may contain newly appended immutable blocks; replacing it would discard valid history. Restore planner artifacts only under a separate corruption-recovery decision.

### 3. Reactivate the accepted test station

```bash
sudo /opt/nzyte-tv/production/nzyte-tv-production-handoff.sh \
  rollback-start-test

systemctl is-enabled nzyte-tv.service
systemctl is-active nzyte-tv.service
systemctl is-enabled nzyte-tv-3b2d.service
systemctl is-active nzyte-tv-3b2d.service
systemctl is-enabled nzyte-tv-3b2e-boot-reconnect.timer
pgrep -x -c nzytetv
pgrep -x -c ffmpeg
```

Require production disabled/inactive, test enabled/active, test recovery enabled, and counts `1`/`1`. Verify the isolated test rolling status and E1 diagnostics, then manually verify moving YouTube video and audible audio.

## Final release gate

Do not create `v0.5.0` during preparation or initial cutover. A release decision requires:

1. reviewed 3B2-F commit and clean build/test results;
2. exact production manifest and initial-state evidence;
3. successful exclusive cutover with no simultaneous broadcaster;
4. rolling claim/resume and replenishment evidence;
5. viewer-confirmed video and audio;
6. production E3 recovery after two separate reboots, at most once per boot;
7. production state, configuration, credentials, and committed-block integrity; and
8. a separately approved merge, push, and annotated `v0.5.0` tag.

## Remaining risks

- Boot recovery remains an operational workaround without viewer-facing YouTube monitoring. It may restart a healthy production stream once after an eligible boot.
- The first rolling claim intentionally abandons an unfinished static queue only when the operator creates the explicit marker and the coordinator independently validates `STOPPED` schema-2 evidence.
- E1 writes progress periodically; the recovery guard waits 35 seconds for two persisted observations and consumes the boot attempt on an inconclusive result.
- Global exact-name process checks require the production Pi to run no unrelated `nzytetv` or FFmpeg process.
- A controlled restart or CP2 resume restarts the current asset from its beginning.
- The staged unit hardening, ARM64 publication, systemd ordering, and full rollback still require Raspberry Pi execution; local tests mock systemd and processes.
