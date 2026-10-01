# Accelerated rolling integration testing

Checkpoint 3B2-C can exercise several real immutable-block handoffs without waiting six hours per boundary. The planner supports one initialization-only test target while retaining the same production planning and publication pipeline.

This is not a production-duration control. New production lineages still default to exactly 21,600 seconds. A non-default lineage is permanently separate and is printed as:

```text
TEST LINEAGE — NON-PRODUCTION DURATION
```

## Create an isolated lineage

Use a dedicated media root, catalog copy, planner manifest, station configuration, CP2 state/lock, rolling state/lock, replenishment sidecar, and test streaming destination. A normalized library may be exposed through a read-only bind mount when its paths and readiness sidecars remain valid. Do not modify or reuse production planning or runtime files.

Initialize a four-minute target:

```bash
/opt/nzyte-tv/app/nzytetv programming rolling init \
  --media-root /srv/nzyte-tv-test/media \
  --base-seed 20261001 \
  --test-block-duration 4m

/opt/nzyte-tv/app/nzytetv programming rolling maintain \
  --media-root /srv/nzyte-tv-test/media

/opt/nzyte-tv/app/nzytetv programming rolling validate \
  --media-root /srv/nzyte-tv-test/media

/opt/nzyte-tv/app/nzytetv programming rolling status \
  --media-root /srv/nzyte-tv-test/media
```

The accepted range is 60–1,800 whole seconds. Examples include `3m`, `4m`, `5m`, and `240s`. Fractional, zero, negative, malformed, and out-of-range values are rejected. The option is rejected by `maintain`, `validate`, `status`, and unrelated commands.

The manifest's existing `targetBlockDurationSeconds` remains the sole duration authority. The selected value flows through the frozen generation intent, input snapshot, schema-version-1 playlist, committed descriptor, and block identity. The scheduler uses complete assets and does not trim media, so an actual block may exceed its nominal target by the duration of the final asset. This can make a nominal four-minute block materially longer when only long presentations satisfy the programming rules.

## Safety and idempotence

- Repeating initialization with the same explicit duration, seed, and genesis leaves manifest bytes and modification time unchanged.
- A different explicit duration is refused without changing the manifest.
- Omitting the option on an existing lineage preserves its persisted duration.
- A production lineage cannot be converted to a test lineage, and a test lineage is not converted by omitting the option.
- Frozen retry work retains its original duration, seed, timestamp, and input snapshot.
- Committed playlists and histories remain immutable and are accepted only through the manifest commit point.
- The three-block window and automatic replenisher operate by sequence: an active block still requires the next two committed blocks regardless of duration.

## Integration acceptance outline

Before any real execution, require `programming rolling validate` to report valid, verify the non-production label and target, and run `station rolling validate` against separate test configuration and runtime paths. Keep the existing production systemd unit disabled and unchanged.

The later manual Pi/YouTube test should use a separate test live event and observe, without displaying process arguments or credentials:

1. one real FFmpeg process and successful audio/video ingest;
2. clean cancellation followed by item-level restart from the accepted CP2 cursor;
3. unexpected child termination followed by bounded CP2 recovery;
4. at least three block completions and exact next-sequence claims;
5. two future committed blocks maintained as execution advances;
6. continued execution of prepared blocks during a temporary planning failure;
7. expected RTMPS reconnect behavior at each immutable block boundary; and
8. no destination in application output, JSON artifacts, or process-list commands.

Use `pgrep -x ffmpeg` or `pgrep -x -c ffmpeg`; never display the full FFmpeg command line. This document describes a future manual acceptance procedure, not a claim that FFmpeg or YouTube testing ran during development.

## Cleanup

Stop the test coordinator cleanly, confirm the test FFmpeg child is gone, and retain any state needed for diagnosis before removing the isolated test tree. Do not delete, rewrite, or move production blocks, production runtime state, the production environment file, or the accepted systemd unit. The duration option itself never installs, enables, starts, or modifies a service.
