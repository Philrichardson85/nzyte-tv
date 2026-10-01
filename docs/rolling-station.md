# Rolling station coordinator and replenishment (Checkpoints 3B2-A/B)

Checkpoint 3B2-A is an opt-in execution coordinator for immutable blocks committed by the [rolling programming planner](rolling-programming.md). It claims exactly one six-hour block, delegates that block to the accepted Checkpoint 2 `StationSupervisor`, confirms durable completion, and then claims the next manifest block. Checkpoint 3B2-B adds an asynchronous runtime host that asks the accepted planner to retain two committed future blocks beyond the active or next-required sequence. Neither component appends to or rewrites the active queue.

The coordinator itself still does not run the planner. The outer runtime host owns the coordinator/background-task lifecycle and delegates all generation to the existing planner. No rolling systemd unit is installed or enabled, one FFmpeg connection is not preserved across block boundaries, and production YouTube boundary acceptance remains deferred to 3B2-C.

## Two separate authorities

Three authoritative durable documents have deliberately different jobs:

- `<media-root>/playlists/rolling/manifest.json` is the sole authority for committed block order and immutable block artifacts.
- `/var/lib/nzyte-tv/state.json` remains Checkpoint 2 schema version 2 and is the sole authority for item-level playback progress, FFmpeg recovery state, and the first item not positively completed.
- `/var/lib/nzyte-tv/rolling-state.json` is schema version 1 and records only block ownership and handoff progress.

An optional `/var/lib/nzyte-tv/rolling-state.json.replenishment.json` schema-version-1 sidecar records advisory buffer/retry health. It is atomically written, secret-redacted, and never authorizes a claim, completion, or manifest append. A sidecar write failure cannot stop healthy execution or corrupt planning state.

There is no multi-file transaction across them. Recovery is idempotent and follows this authority order: verify the manifest block, bind it to the rolling claim, inspect CP2 durable evidence, and only then advance rolling state.

## Configuration

Copy `deploy/config/rolling-station.json.example` to a root-controlled operational location such as `/etc/nzyte-tv/rolling-station.json`:

```json
{
  "schemaVersion": 1,
  "stationConfigPath": "/etc/nzyte-tv/station.json",
  "plannerId": "00112233445566778899aabbccddeeff",
  "rollingStatePath": "/var/lib/nzyte-tv/rolling-state.json"
}
```

Replace `plannerId` with the exact 32-character lineage printed by:

```bash
/opt/nzyte-tv/app/nzytetv programming rolling status \
  --media-root /srv/nzyte-tv/media
```

The referenced static `station.json` continues to supply `mediaRoot`, `libraryRoot`, and the shared CP2 `statePath`. Its static playlist list remains valid for ordinary `station run`; the rolling coordinator replaces that list only in the in-memory configuration passed to `StationSupervisor` for one claimed block. Static configuration schema version 1 is unchanged.

Neither configuration contains `NZYTE_TV_RTMP_URL`. A live run still reads that value from the environment, classifies it without displaying it, and passes it only to the existing broadcaster.

## Commands

```bash
/opt/nzyte-tv/app/nzytetv station rolling validate \
  --config /etc/nzyte-tv/rolling-station.json

/opt/nzyte-tv/app/nzytetv station rolling status \
  --config /etc/nzyte-tv/rolling-station.json

/opt/nzyte-tv/app/nzytetv station rolling run \
  --config /etc/nzyte-tv/rolling-station.json
```

`validate` and `status` are read-only and never invoke the planner or create files. Validation checks both configurations, planner lineage, every committed block's hashes and immutable descriptor, history/input references, path containment, selected-media readiness, the existing `BroadcastPlanner`, rolling/CP2 consistency, FFmpeg availability, and destination classification. It uses each block's frozen programming snapshot; a newly broken or changed visible `programming.json` does not invalidate a block already committed. Future-generation health is reported separately from committed execution readiness.

The destination is reported only as `NOT CONFIGURED`, `CONFIGURED / VALID`, or `CONFIGURED / INVALID`. Validation may inspect non-secret state without a destination. `run` requires a valid RTMP/RTMPS destination.

No rolling systemd service is supplied in 3B2-A/B. Do not repoint or modify the accepted static `nzyte-tv.service` as part of this checkpoint.

## Runtime host and moving buffer

`station rolling run` first starts the accepted coordinator and waits until its lifetime lock is confirmed. Only the lock owner starts the background replenisher; a competing rolling process cannot generate under the guise of execution. When the coordinator exits, the host cancels and awaits replenishment and returns the coordinator's original exit result. Background exceptions are observed and redacted rather than becoming unobserved task failures.

The prepared-window target comes from the manifest and is not duplicated in rolling configuration. With the current window `W = 3`, the future target is `F = W - 1 = 2`. The durable buffer calculation is:

```text
next required = (last completed sequence + 1), or 1
anchor        = complete active claim, otherwise next required
required      = anchor + F
deficit       = max(0, required - highest contiguous committed sequence)
```

`claimed`, `executing`, `stopped`, and restartable `failed` states with a complete claim anchor on that active sequence. `advancing` and `waitingForBlock` anchor on the next required sequence. Contradictory or partial identities are rejected rather than guessed. Planned history is never interpreted as execution evidence.

Durable execution-state writes coalesce wake-ups for the replenisher. A 45-second periodic read remains a fallback for missed signals and externally appended blocks. At startup the accepted planner first reconciles staging and exact orphans; FFprobe and generation dependencies stay lazy if the buffer is already healthy. After generation the target is recalculated because execution may have advanced while planning ran.

Execution, planning, and CP2 recovery retain separate locks. The coordinator lifetime lock does not replace the planner lock; manual and automatic maintenance serialize through the same planner lock. The replenisher never holds the CP2 station lock, and the coordinator never holds the planner lock, so generation cannot block active FFmpeg execution through nested lock ownership.

## Runtime-state schema and phases

The rolling document stores:

- `schemaVersion`, fixed at 1;
- `plannerId`;
- `phase`;
- the active block's `sequence`, `blockId`, and runtime `queueId` as one all-or-nothing group;
- the last completed block's sequence and ID;
- claim, completion, update, and optional accepted-cutover timestamps;
- a redacted transition error and `restartable` or `permanent` failure disposition;
- the source static queue ID when an operator explicitly accepts a stopped-static cutover.

It never duplicates the CP2 item cursor, station PID, FFmpeg PID, heartbeat, recovery count, RTMP destination, or process command line.

Legal persisted phases are:

| Phase | Meaning |
|---|---|
| `claimed` | Exact block and queue identities are durable; playback may not have started. |
| `executing` | The coordinator has handed the claimed plan to `StationSupervisor`. |
| `stopped` | Clean cancellation retained the claim and CP2 resume cursor. |
| `advancing` | The prior block has positive completion evidence and no active claim remains. |
| `waitingForBlock` | The exact required next sequence is not committed yet. |
| `failed` | A restartable runtime failure or permanent safety failure was recorded. The active identity is retained when one exists. |

A missing document means the coordinator is uninitialized.

## Claim protocol

For the required next sequence, the coordinator:

1. Loads the manifest and verifies the configured lineage.
2. Resolves that exact manifest entry; it never substitutes a later sequence.
3. Verifies descriptor and block identity, playlist/input/history hashes, portable containment and symlink safety, captured media readiness, item count, and broadcast plan readiness.
4. Builds the immutable `BroadcastPlan` through the existing planner and calculates the existing `BroadcastQueueIdentity`.
5. Reloads the manifest and verifies that the selected entry is unchanged. Appended later blocks are harmless.
6. Atomically writes the active sequence, block ID, and queue ID before invoking `StationSupervisor`.

The claim proves ownership only. It does not prove that FFmpeg started or that any item played.

## Completion and sealing

A block is positively complete only when durable schema-v2 CP2 state matches the claim and all of these are true:

- queue ID matches;
- queue item count matches the verified plan;
- `lastCompletedGlobalIndex` is the final zero-based item index;
- `resumeGlobalIndex` is `null`.

Normal CP2 `completed` state satisfying those identities is sufficient. The coordinator also handles the narrow crash window after the final `ItemCompleted` cursor write but before `StationSupervisor` writes its terminal state. For a nonterminal document, it verifies that the prior supervisor is gone, exclusively acquires the exact CP2 `<statePath>.lock`, rereads and revalidates the document under that lock, applies only the accepted `SetCompleted` fields, writes atomically, and releases the lock. It never seals an incomplete queue or state owned by a live supervisor, and it never holds the lock while running the supervisor.

After CP2 completion is confirmed, rolling completion is written atomically, the active claim is cleared, and only the next contiguous manifest sequence may be claimed. A crash between any of those writes is reconciled without replaying a positively completed block or skipping an unconfirmed one.

## Static/rolling mutual exclusion

The coordinator holds a separate lifetime lock beside the shared CP2 state file, preventing two rolling coordinators from competing. Actual playback still uses the original `StationSupervisor`, the exact same CP2 state path, and the exact same `<statePath>.lock` as static operation. Therefore static and rolling supervisors cannot both own broadcast execution.

The CP2 lock is intentionally released between blocks. If static operation acquires it at that boundary, reconciliation detects the changed/live CP2 state or the next supervisor fails to acquire the lock. The rolling coordinator refuses rather than overwriting the other queue.

## Initial cutover

The first rolling claim is allowed when CP2 state is absent, or when a schema-v2 queue is safely `completed` and its recorded process is not alive. An unfinished `stopped` static queue is resumable and is refused by default.

To intentionally abandon only that stopped static queue on the very first rolling run:

```bash
/opt/nzyte-tv/app/nzytetv station rolling run \
  --config /etc/nzyte-tv/rolling-station.json \
  --accept-stopped-static-cutover
```

The option is accepted only when no rolling state exists, CP2 is schema version 2 and `stopped`, and the recorded process is dead. The source queue ID and acceptance time are persisted. It cannot override a live process, schema-v1 state, an interrupted `starting`/`broadcasting`/`failed` queue, invalid state, or an existing rolling lineage. If the first block is not yet committed, the accepted audit survives `waitingForBlock` and is reverified before claim.

Rollback before any rolling state is persisted is simply continued static operation. Once a claim or `waitingForBlock` state exists, stop the coordinator and inspect both state documents; do not delete or edit them to force a mode change. Resolve any active claim safely before returning to a different static queue.

## Missing blocks and failures

When the exact next sequence is absent, the coordinator enters `waitingForBlock` and polls after approximately 5, 15, 30, and then at most 60 seconds. It remains cancellable, never invokes the planner directly, never replays the completed block, and never skips to another sequence. The background replenisher independently continues trying to publish that exact missing sequence through the accepted atomic planner; a later manifest publication is observed by normal coordinator polling.

Replenishment retries transient planner-lock, mount, I/O, and media-tool failures after approximately 5, 15, 30, and then 60 seconds. Backoff resets after successful maintenance or observable manifest progress. Invalid visible programming configuration or insufficient eligible inventory is reported as `BLOCKED` for future generation without invalidating committed blocks. Frozen-intent recovery remains authoritative. Committed-chain corruption is a `SAFETY FAILURE`: automatic append stops and no replacement sequence is guessed, while an already independently handed-off current block is not killed solely for that future-planning error.

Exit codes are:

- `0`: clean cancellation/stop;
- `1`: restartable runtime or temporary media-unavailability failure;
- `78`: permanent configuration, identity, concurrency, corruption, or safety failure.

Missing/corrupt committed artifacts, identity replacement, contradictory CP2 state, and concurrent ownership are permanent. An unavailable media mount is restartable when distinguishable from verified artifact mutation. Selected media that no longer matches its frozen readiness snapshot is a permanent integrity failure.

## Crash reconciliation matrix

| Boundary | Reconciliation |
|---|---|
| Before claim | No claim exists; retry the same next manifest sequence. |
| Claim persisted before supervisor | Resume the same claim; do not infer playback. |
| First item or middle of block | CP2 restarts the first item not positively completed; the claim is unchanged. |
| Clean stop or graceful reboot | Rolling phase becomes `stopped`; CP2 cursor and claim remain resumable. |
| Final item cursor persisted before terminal CP2 state | Seal only after dead-process and exclusive-lock revalidation. |
| CP2 completed before rolling completion | Record rolling completion; never replay the block. |
| Rolling completion before next claim | Claim exactly the next contiguous block. |
| Next claim persisted before first item | Resume that next claim; the previous block stays completed. |
| Coordinator dies after next block starts | Reconcile that block through its matching CP2 queue ID/cursor. |
| Manifest grows | Current claim is unchanged; new entries are considered only at later boundaries. |
| Required next block absent | Wait with bounded polling. |
| Required next block corrupt/replaced | Fail permanently; do not substitute another block. |
| Media mount unavailable | Return restartable failure when recognized as temporary; do not change claim. |
| Planner unavailable or future policy invalid | Prepared committed blocks remain executable; replenishment records degraded/blocked health and retries as appropriate. |
| Static station already running | Refuse through shared state/process/lock checks. |
| Second rolling coordinator | Refuse through the coordinator lifetime lock. |
| CP2 and rolling state disagree | Fail permanently without starting execution. |
| Claimed block removed/replaced | Fail permanently; the claim is never rebound. |

## Security

Rolling configuration, runtime state, status, and diagnostics never contain a destination or stream key. Do not inspect FFmpeg with commands that print complete command lines; its destination is an argument. Safe checks remain:

```bash
pgrep -x ffmpeg
pgrep -x -c ffmpeg
```

The first prints only matching PIDs and the second only a count.

## Development and Pi acceptance boundary

Automated tests use a fake `IRollingBlockExecutor` that writes realistic CP2 states and injects crashes at claim, execution, final-item, sealing, rolling-completion, and next-claim boundaries. A separate fake maintainer exercises moving targets, retries, cancellation, and six-block execution without generating six-hour media. Production execution uses a thin adapter around the unchanged `StationSupervisor`, and production replenishment delegates to the unchanged rolling planner; neither fake is another broadcaster or scheduler.

For later Raspberry Pi acceptance, keep the existing production unit unchanged and run the opt-in command manually. Validate the configuration first, record both status documents, stop static service execution, then test clean cancellation, process kill/restart, and a block boundary with non-production credentials. Confirm one FFmpeg process, item-level restart within the same block, exactly-once logical advancement, and no credential in output or JSON. Do not treat that procedure as completed acceptance until it has actually been run.

Checkpoint 3B2-B now replenishes the future-block buffer during opt-in rolling execution. Checkpoint 3B2-C will evaluate the FFmpeg/RTMPS reconnect at a six-hour boundary against YouTube. No production service is installed or enabled by 3B2-A/B.
