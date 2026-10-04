# Checkpoint 3B2-E3 boot-only reconnection

Checkpoint 3B2-E3 is an optional workaround for the isolated `nzyte-tv-3b2d.service` Raspberry Pi test. It is not a root-cause fix, YouTube monitor, production feature, or change to NZYTE TV. It installs a separate timer and oneshot service which may perform one guarded stop/start approximately 150 seconds after a new machine boot.

The production `nzyte-tv.service`, the isolated test service, their drop-ins, FFmpeg arguments, application binaries, CP2 state, rolling state, committed blocks, and scheduler are not modified by E3.

## Lifecycle and fail-closed rules

`nzyte-tv-3b2e-boot-reconnect.timer` has only `OnBootSec=150s`; it has no periodic trigger and is not related to manual restarts of the broadcast service. When it fires, the oneshot script:

1. acquires an exclusive `flock`;
2. reads `/proc/sys/kernel/random/boot_id`;
3. exits when that boot ID is already recorded;
4. atomically records a new boot ID before readiness checks or service control, consuming the one E3 slot for that boot;
5. requires production to be exactly `inactive` and `disabled`;
6. requires the isolated service to be enabled and active, its MainPID to equal the sole `nzytetv` PID, exactly one FFmpeg PID, and that FFmpeg process's `/proc` parent PID to equal the service MainPID;
7. reads only an allowlisted E1 snapshot—attempt ID, FFmpeg PID, output time, and last-advancing time—and requires the live FFmpeg PID to match;
8. waits 35 seconds and requires the same attempt and PID to advance with recent progress;
9. performs one bounded stop, verifies the service and both processes are gone, starts it, and verifies the service and both processes return; and
10. writes a concise schema-version-1 outcome containing only UTC time, boot ID, result, and a fixed reason code.

Any missing prerequisite consumes that boot's slot and records a safe skip. It is intentionally not retried during the same boot. The marker is written before service control, so a killed script or failed action cannot create a restart loop. A failed initial start permits only one bounded `start` restoration attempt. CP2 remains solely responsible for resuming programming position.

E3 does not inspect YouTube. A successful outcome means only that the guarded local stop/start completed.

## Files and fixed paths

Repository assets:

```text
deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.sh
deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-read-diagnostics.py
deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.service
deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.timer
```

Installed test-only paths:

```text
/opt/nzyte-tv/integration/3b2d/e3/
/opt/nzyte-tv/integration/3b2d/state/e3/last-attempt-boot-id
/opt/nzyte-tv/integration/3b2d/state/e3/last-outcome.env
/etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.service
/etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.timer
```

The E1 diagnostic input remains:

```text
/opt/nzyte-tv/integration/3b2d/state/broadcast-diagnostics.json
```

## Pre-install verification

Do not continue unless the checkout is the approved E3 commit, production reports `disabled` and `inactive`, and the isolated test reports one application and one FFmpeg process:

```bash
cd /opt/nzyte-tv/src
git branch --show-current
git rev-parse HEAD
git status --short

systemctl is-enabled nzyte-tv.service
systemctl is-active nzyte-tv.service
systemctl is-active nzyte-tv-3b2d.service
pgrep -x -c nzytetv
pgrep -x -c ffmpeg

test -x /opt/nzyte-tv/app-candidate-3b2e1-ebb7638/nzytetv
test -s /opt/nzyte-tv/integration/3b2d/state/broadcast-diagnostics.json
```

Expected service/count results are `disabled`, `inactive`, `active`, `1`, and `1`. Do not use process commands that print arguments.

Validate the repository assets without starting anything:

```bash
bash -n deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.sh
python3 deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-read-diagnostics.py \
  /opt/nzyte-tv/integration/3b2d/state/broadcast-diagnostics.json
```

The diagnostic reader prints only attempt ID, PID, output microseconds, and last-advancing Unix time. It does not print stderr, a destination, or arguments.

## Installation without interrupting the current stream

Install into new E3-only paths. Do not edit either existing service:

```bash
cd /opt/nzyte-tv/src

sudo install -d -o root -g root -m 0755 \
  /opt/nzyte-tv/integration/3b2d/e3
sudo install -d -o root -g root -m 0700 \
  /opt/nzyte-tv/integration/3b2d/state/e3

sudo install -o root -g root -m 0755 \
  deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.sh \
  /opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-boot-reconnect.sh
sudo install -o root -g root -m 0755 \
  deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-read-diagnostics.py \
  /opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-read-diagnostics.py

sudo /opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-boot-reconnect.sh seed
sudo cmp -s \
  /proc/sys/kernel/random/boot_id \
  /opt/nzyte-tv/integration/3b2d/state/e3/last-attempt-boot-id
echo "Seeded current boot without service control."

sudo install -o root -g root -m 0644 \
  deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.service \
  /etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.service
sudo install -o root -g root -m 0644 \
  deploy/checkpoint-3b2-e3/nzyte-tv-3b2e-boot-reconnect.timer \
  /etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.timer

sudo systemd-analyze verify \
  /etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.service \
  /etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.timer
sudo systemctl daemon-reload
sudo systemctl enable --now nzyte-tv-3b2e-boot-reconnect.timer
```

Enabling the timer after its 150-second boot deadline may invoke the oneshot immediately. The seeded boot ID makes that invocation a no-op. Verify the existing stream was not interrupted:

```bash
systemctl is-active nzyte-tv-3b2d.service
pgrep -x -c nzytetv
pgrep -x -c ffmpeg
sudo cat /opt/nzyte-tv/integration/3b2d/state/e3/last-outcome.env

systemctl show nzyte-tv-3b2e-boot-reconnect.service \
  --property=Result \
  --property=ExecMainStatus \
  --property=ActiveState \
  --property=SubState \
  --no-pager
```

The outcome must remain `result=seeded` and `reason=current-boot-protected`. Do not accept installation if the test application's PIDs changed.

## Reboot acceptance and safe evidence

Create an evidence directory before reboot without copying credentials, environment files, journals, process arguments, or raw diagnostics:

```bash
E3_EVIDENCE=/opt/nzyte-tv/integration/3b2d/evidence/e3-reboot-1
sudo install -d -o root -g root -m 0700 "$E3_EVIDENCE"

date -u +%Y-%m-%dT%H:%M:%SZ | sudo tee "$E3_EVIDENCE/before-utc.txt" >/dev/null
cat /proc/sys/kernel/random/boot_id | sudo tee "$E3_EVIDENCE/before-boot-id.txt" >/dev/null
systemctl is-enabled nzyte-tv.service | sudo tee "$E3_EVIDENCE/production-enabled-before.txt" >/dev/null
systemctl is-active nzyte-tv.service | sudo tee "$E3_EVIDENCE/production-active-before.txt" >/dev/null
systemctl show nzyte-tv-3b2d.service \
  --property=ActiveState \
  --property=SubState \
  --property=MainPID \
  --property=ExecMainStartTimestamp \
  --no-pager | sudo tee "$E3_EVIDENCE/test-service-before.txt" >/dev/null
pgrep -x -c nzytetv | sudo tee "$E3_EVIDENCE/nzytetv-count-before.txt" >/dev/null
pgrep -x -c ffmpeg | sudo tee "$E3_EVIDENCE/ffmpeg-count-before.txt" >/dev/null
python3 /opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-read-diagnostics.py \
  /opt/nzyte-tv/integration/3b2d/state/broadcast-diagnostics.json \
  | sudo tee "$E3_EVIDENCE/e1-safe-before.txt" >/dev/null

sudo reboot
```

After reconnecting to the Pi, allow 220 seconds for the 150-second timer, 35-second observation, controlled restart, and bounded startup verification:

```bash
sleep 220

E3_EVIDENCE=/opt/nzyte-tv/integration/3b2d/evidence/e3-reboot-1
date -u +%Y-%m-%dT%H:%M:%SZ | sudo tee "$E3_EVIDENCE/after-utc.txt" >/dev/null
cat /proc/sys/kernel/random/boot_id | sudo tee "$E3_EVIDENCE/after-boot-id.txt" >/dev/null
sudo cp /opt/nzyte-tv/integration/3b2d/state/e3/last-outcome.env \
  "$E3_EVIDENCE/last-outcome.env"
sudo cp /opt/nzyte-tv/integration/3b2d/state/e3/last-attempt-boot-id \
  "$E3_EVIDENCE/last-attempt-boot-id.txt"

systemctl is-enabled nzyte-tv.service | sudo tee "$E3_EVIDENCE/production-enabled-after.txt" >/dev/null
systemctl is-active nzyte-tv.service | sudo tee "$E3_EVIDENCE/production-active-after.txt" >/dev/null
systemctl show nzyte-tv-3b2e-boot-reconnect.service \
  --property=Result \
  --property=ExecMainStatus \
  --property=ActiveState \
  --property=SubState \
  --property=ExecMainStartTimestamp \
  --no-pager | sudo tee "$E3_EVIDENCE/e3-service-after.txt" >/dev/null
systemctl show nzyte-tv-3b2d.service \
  --property=ActiveState \
  --property=SubState \
  --property=MainPID \
  --property=ExecMainStartTimestamp \
  --no-pager | sudo tee "$E3_EVIDENCE/test-service-after.txt" >/dev/null
pgrep -x -c nzytetv | sudo tee "$E3_EVIDENCE/nzytetv-count-after.txt" >/dev/null
pgrep -x -c ffmpeg | sudo tee "$E3_EVIDENCE/ffmpeg-count-after.txt" >/dev/null
python3 /opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-read-diagnostics.py \
  /opt/nzyte-tv/integration/3b2d/state/broadcast-diagnostics.json \
  | sudo tee "$E3_EVIDENCE/e1-safe-after.txt" >/dev/null

sudo cat /opt/nzyte-tv/integration/3b2d/state/e3/last-outcome.env
sudo cmp -s \
  /proc/sys/kernel/random/boot_id \
  /opt/nzyte-tv/integration/3b2d/state/e3/last-attempt-boot-id
echo "E3 consumed exactly this boot ID."
```

Required local results are `result=success`, `reason=guarded-reconnection-completed`, production `disabled`/`inactive`, test service `active`, and both counts equal to `1`. Use the existing isolated 3B2-D status procedure to capture CP2 cursor, rolling claim, committed-block chain, and replenishment evidence before and after. E3 creates or changes none of those files.

Local success is not final acceptance. In YouTube, manually confirm moving video and audible audio, not merely healthy ingest. Repeat the complete evidence procedure as `e3-reboot-2` on a later reboot and require the same result.

## Inspect or disable

Safe inspection does not require journal or environment output:

```bash
systemctl list-timers --all nzyte-tv-3b2e-boot-reconnect.timer --no-pager
systemctl show nzyte-tv-3b2e-boot-reconnect.service \
  --property=Result \
  --property=ExecMainStatus \
  --property=ActiveState \
  --property=SubState \
  --property=ExecMainStartTimestamp \
  --no-pager
sudo cat /opt/nzyte-tv/integration/3b2d/state/e3/last-outcome.env
sudo cat /opt/nzyte-tv/integration/3b2d/state/e3/last-attempt-boot-id
```

Disable future boot actions while retaining installed files and evidence:

```bash
sudo systemctl disable --now nzyte-tv-3b2e-boot-reconnect.timer
sudo systemctl reset-failed nzyte-tv-3b2e-boot-reconnect.service
```

This does not stop or restart `nzyte-tv-3b2d.service`.

## Complete rollback

First disable the timer as above. Then remove only the dedicated E3 assets:

```bash
sudo systemctl reset-failed nzyte-tv-3b2e-boot-reconnect.service
sudo rm -f /etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.timer
sudo rm -f /etc/systemd/system/nzyte-tv-3b2e-boot-reconnect.service
sudo systemctl daemon-reload

sudo rm -f /opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-boot-reconnect.sh
sudo rm -f /opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-read-diagnostics.py
sudo rmdir /opt/nzyte-tv/integration/3b2d/e3

sudo rm -f /opt/nzyte-tv/integration/3b2d/state/e3/last-attempt-boot-id
sudo rm -f /opt/nzyte-tv/integration/3b2d/state/e3/last-outcome.env
sudo rm -f /opt/nzyte-tv/integration/3b2d/state/e3/reconnect.lock
sudo rmdir /opt/nzyte-tv/integration/3b2d/state/e3
```

Do not remove the parent integration/state directories, E1 diagnostics, application candidate, test service or drop-ins, CP2/rolling state, programming files, production unit, production application, or credentials.

## Remaining limitations

- E3 cannot tell whether YouTube is stuck or healthy. On every eligible reboot it may restart a viewer-healthy test stream once.
- The one boot slot is consumed on any failed or inconclusive prerequisite; recovery waits until another machine boot or an explicit operator action outside E3.
- E1 persists progress at an interval, so E3 uses a 35-second observation rather than a fast poll.
- Global exact-name process counts require this isolated Pi to run no other `nzytetv` or FFmpeg instance.
- Python 3, `flock`, `systemctl`, `pgrep`, and GNU `date` are required; the verified Ubuntu 24.04 environment supplies them, but installation must verify them.
- A controlled resume restarts the current item according to existing CP2 semantics and may replay part of that item.
- The operational YouTube failure remains unexplained; this workaround must not be promoted to production without a separate decision.
