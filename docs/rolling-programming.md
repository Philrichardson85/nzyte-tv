# Rolling programming planner (Checkpoint 3B1)

Checkpoint 3B1 prepares immutable six-hour programming blocks ahead of station execution. It is a planning system, not a rolling station. Its commands never start FFmpeg, change `station.json`, append to a running Checkpoint 2 queue, or advance runtime state. Static station and manual `build-playlist` operation remain unchanged. Checkpoint 3B2-A provides a separate opt-in coordinator that consumes committed blocks, and Checkpoint 3B2-B invokes this same accepted planner asynchronously to replenish that coordinator's future buffer; see [rolling-station.md](rolling-station.md).

For a new lineage before its first execution claim, the default target is three committed blocks:

```text
activation candidate
future block 1
future block 2
```

During rolling execution, the policy is interpreted as one active (or next-required) block plus two committed future blocks. The execution cursor is not part of 3B1 or its portable manifest; it lives in a separate runtime document.

## Commands

Choose the genesis history explicitly. Omitting `--history` means an intentional empty planned-history genesis; NZYTE TV never auto-discovers or imports an ordinary `history.json`:

```bash
/opt/nzyte-tv/app/nzytetv programming rolling init \
  --media-root /srv/nzyte-tv/media \
  --base-seed 20261001
```

Or import an existing planned-history document:

```bash
/opt/nzyte-tv/app/nzytetv programming rolling init \
  --media-root /srv/nzyte-tv/media \
  --history /srv/nzyte-tv/media/playlists/history.json \
  --base-seed 20261001
```

`--base-seed` is optional. When omitted, a random signed integer is generated once and persisted in the manifest. It never changes on later maintenance. Initialization is idempotent for the same explicit genesis and seed and refuses to replace or fork an existing manifest.

Prepare the initial buffer, validate it, and inspect it:

```bash
/opt/nzyte-tv/app/nzytetv programming rolling maintain \
  --media-root /srv/nzyte-tv/media

/opt/nzyte-tv/app/nzytetv programming rolling validate \
  --media-root /srv/nzyte-tv/media

/opt/nzyte-tv/app/nzytetv programming rolling status \
  --media-root /srv/nzyte-tv/media
```

`maintain` generates sequentially until the manifest's default three-block minimum is satisfied. The target means "ensure at least this many contiguous committed blocks": if automatic replenishment has already extended the manifest beyond three, ordinary `maintain` still reconciles staging/orphans and succeeds without shrinking or rewriting anything. It uses FFprobe through the accepted library loader only when generation is required, never invokes FFmpeg, and never reads an RTMP destination. Repeating it at a satisfied target changes no committed playlist, history, input snapshot, descriptor, hash, or modification time. `validate` and `status` are read-only. Status reports the lineage, schema, base seed, target duration, buffer target, committed range, next sequence, history-head prefix, nominal and actual prepared duration, per-block policy revision, currently visible policy revision, staging/quarantine counts, and validation health. It states that execution/handoff is not performed by the planner.

## Automatic relative replenishment

`station rolling run` now hosts a background replenisher after, and only after, the rolling coordinator has acquired its lifetime lock. The coordinator and planner remain independent: execution owns the coordinator and CP2 locks; generation owns the existing planner lock. No component holds the station lock while generating or the planner lock while broadcasting.

Let `W` be the manifest prepared-window target (currently 3), so the future target is `F = W - 1` (currently 2). The replenisher anchors on the complete active claim when one exists, otherwise on the next sequence after the last positively completed block. It requires commitment through `anchor + F`. Thus active block 1 requires blocks through 3, active block 2 requires blocks through 4, and a completed block 3 awaiting block 4 requires blocks through 6. It never moves the execution cursor to satisfy this planning target.

At process startup the planner performs its accepted reconciliation even when the buffer is healthy; expensive FFprobe and snapshot-generation dependencies are initialized lazily only when a real deficit remains. Durable rolling-state changes wake the replenisher, while a 45-second consistency check catches missed signals and blocks appended by another process. Transient failures retry after approximately 5, 15, 30, and then 60 seconds. Manual and automatic maintenance serialize through the same exclusive planner lock.

A planner failure does not remove or invalidate an already committed executable block. Invalid visible policy or insufficient inventory blocks only future generation; frozen-intent mismatches retain the accepted deterministic retry evidence; committed-chain corruption stops automatic append rather than guessing. If the buffer reaches zero, the execution coordinator remains in `waitingForBlock` while replenishment continues trying the exact missing sequence.

## Portable layout

All planning artifacts are non-secret and portable beneath the selected media root:

```text
<media-root>/playlists/rolling/
|-- manifest.json
|-- .locks/
|   `-- planner.lock
|-- .staging/
|   `-- <12-digit-sequence>/
|       |-- intent.json
|       |-- input.json
|       |-- playlist.json
|       |-- history-after.json
|       `-- publish/
|-- blocks/
|   `-- <12-digit-sequence>-<block-id>/
|       |-- block.json
|       |-- input.json
|       `-- playlist.json
|-- history/
|   `-- <sha256>.json
`-- orphaned/
```

The planner stores no RTMP/RTMPS destination, stream key, process command line, PID, FFmpeg PID, station heartbeat, or runtime resume cursor. `/var/lib/nzyte-tv` remains station runtime territory and is not used by 3B1.

The lock filename is not authority by itself. A writer holds an exclusive `FileShare.None` handle for the complete initialize or maintain operation. A stale filename after a crash is harmless because the operating system releases the handle. Generation is deliberately sequential because block N+1 must consume block N's planned-history result.

## Manifest and commit semantics

`manifest.json` schema version 1 is the sole commit point. A file merely present under `blocks/` or `history/` is uncommitted until the manifest references it. The manifest records:

- one stable `plannerId` lineage;
- the persisted `baseSeed`;
- the six-hour target block duration;
- the three-block prepared target;
- `nextSequence`;
- immutable genesis and current `historyHead` references; and
- ordered committed block records.

Each block record carries its sequence, block and parent identities, derived seed, relative artifact paths and SHA-256 hashes, target and actual durations, planned schedule boundaries, item count, before/after history references, catalog/programming/inventory snapshot hashes, programming schema/revision or the explicit legacy marker, planner algorithm version, and an audit generation timestamp.

The manifest is written and flushed to a same-directory temporary file and atomically moved over the live file. That replacement is the logical commit. A crash can therefore leave the old valid manifest or the new valid manifest. Immutable final artifacts may safely exist before the commit; their existence alone never advances the ledger or history head.

## Block identity and deterministic retry

`blockId` is a lowercase SHA-256 digest over an explicitly framed identity format version, planner lineage, sequence, parent/genesis position, derived seed, target duration, planned schedule start, ordered generated schedule material, before/after history hashes, catalog snapshot hash, programming snapshot hash or legacy marker, inventory snapshot hash, and planner/scheduler algorithm version.

It excludes absolute paths, filesystem modification times, process IDs, destination credentials, the wall-clock audit generation timestamp, and random process identity. Playlist-file SHA-256 is stored separately to detect byte corruption. This identity does not replace Checkpoint 2 `BroadcastQueueIdentity`; the latter remains the runtime identity calculated later from a final station queue and final Pi paths.

Each block seed is derived by SHA-256 from a domain separator, lineage, persisted base seed, and block sequence, then interpreted as a signed big-endian 32-bit scheduler seed. It does not use time, PID, programming revision, or destination data.

Before scheduling, the planner freezes a durable intent with one intent ID, sequence, derived seed, parent/history identity, exact input-snapshot identity, algorithm version, one audit timestamp, planned schedule start, and target duration. The captured input contains the exact catalog and programming JSON (or a legacy marker), deterministic eligible/excluded inventory, relative asset identity and duration data, sidecar/readiness fingerprints, history-before state, and hashes. A retry reads that same intent and snapshot instead of scanning newer inputs. If selected media or its readiness sidecars no longer match the frozen snapshot, the retry fails visibly rather than silently changing the block.

## Planned history transaction

History remains planned scheduling history, never actual playback history. Snapshots are immutable and content addressed:

```text
historyBefore(N)
    -> deterministic generation of N
historyAfter(N) == historyBefore(N+1)
```

The history head moves in the same manifest replacement that commits its block. A history file published before that replacement is harmless and uncommitted. This preserves exact-asset, song-family adjacency/lookback, short-run, bumper, promo, and vlog context across block boundaries without involving schema-version-2 station runtime state.

## Generation transaction and recovery

One maintenance transaction does the following for each required block:

1. hold the exclusive planner lock;
2. load and validate the manifest, then reconcile interrupted work;
3. use the exact `nextSequence`, parent, and history head;
4. capture catalog, programming, eligible inventory, durations, readiness, and history inputs;
5. durably write the frozen intent;
6. call the accepted playlist generator—there is no second scheduler;
7. stage the schema-version-1 playlist, history-after snapshot, input snapshot, and descriptor;
8. recheck selected media/readiness and validate the playlist through the existing `BroadcastPlanner` against the real library root;
9. calculate hashes and `blockId`;
10. publish immutable final history and block artifacts without overwrite; and
11. atomically replace the manifest to add the block, advance `historyHead`, and increment `nextSequence`.

On restart, incomplete disposable `.partial` files are removed. A durable intent is retried with its frozen input. A complete final block in exactly the next sequence/parent/history position can be verified and adopted. A new manifest wins over stale staging. A history snapshot alone never advances history. Corrupt or inconsistent artifacts are quarantined and reported; multiple plausible next blocks cause a refusal to guess. Committed artifacts are never overwritten and are retained indefinitely in 3B1. There is no prune command or emergency withdrawal/fork operation.

## New content and policy latency

Committed blocks are immutable. If three blocks already exist and a new eligible asset arrives, or a campaign, Do Not Air, presentation weight, or other `programming.json` setting changes, all three stay byte-for-byte unchanged. The latest valid catalog, library, and programming policy is captured only for the next block that has not yet been generated.

That means operational latency can be roughly the entire prepared future window. This is intentional for safe V1 operation. Urgent withdrawal of a prepared block is not implemented: automatic replenishment never regenerates or withdraws committed blocks.

## Raspberry Pi acceptance procedure

Do not enable or alter `nzyte-tv.service` for this test. Checkpoint 3B1 does not run the station.

1. Publish/install the candidate, then confirm no planner is running and record the safe FFmpeg count with `pgrep -x -c ffmpeg`.
2. Explicitly choose empty genesis or an imported planned history and run `programming rolling init` against the production media root. Never rely on auto-discovery.
3. Run `programming rolling maintain`. Confirm exactly three committed blocks totaling approximately 18 nominal hours.
4. Run `programming rolling validate` and require `VALID`, then run `programming rolling status` and inspect sequence, parent/history chain, actual durations, policy revisions, staging, and quarantine.
5. Capture hashes and modification times for `manifest.json`, committed `blocks/`, and referenced `history/`. Run `maintain` again and prove committed bytes and mtimes are unchanged.
6. From two shells, start `maintain` concurrently while generation is required. One writer must hold the lock and the other must fail clearly; no duplicate block may be committed. A leftover `planner.lock` filename after exit must not block a later run.
7. During a disposable acceptance lineage, stop only the planner process while `.staging/` contains a durable intent. Restart `maintain`; verify it retries the same sequence, seed, audit timestamp, and frozen snapshot or adopts exact published artifacts.
8. Change the campaign or another policy revision after three blocks are committed. Prove blocks 1–3 remain unchanged. Use an isolated integration-test lineage or the service-level controlled target override—not a hand-edited production manifest—to generate a fourth block and prove only that block captures the new revision.
9. Add one normalized, metadata-resolved, technically eligible test asset. Again use an isolated controlled fourth-block test and prove existing blocks remain unchanged while the later input snapshot sees the asset.
10. Validate every committed playlist through `programming rolling validate`; it invokes the existing `BroadcastPlanner` and must report no missing, invalid, unready, traversal, or link-escape issue.
11. Confirm `pgrep -x -c ffmpeg` is unchanged. No rolling command should launch FFmpeg.
12. Require rolling validation to pass. It rejects RTMP/RTMPS URLs and runtime-state fields in planning artifacts; do not print or search for a real destination value.

These steps are a future real-Pi acceptance plan, not a claim that they have already run on the production library.

## Scope boundary

Checkpoint 3B1 does not consume blocks, choose an active block, update `station.json`, start FFmpeg, enable systemd, create an air-history log, withdraw committed programming, or monitor YouTube. The separate Checkpoint 3B2-A coordinator adds safe activation and handoff while preserving Checkpoint 2's immutable queue epoch and item-level resume guarantees. Checkpoint 3B2-B composes automatic future-buffer replenishment around those accepted services; it does not alter an active queue, install a production service, preserve one FFmpeg connection across block boundaries, or perform YouTube boundary acceptance.
