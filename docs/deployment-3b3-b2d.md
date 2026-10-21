# Checkpoint 3B3-B2-D production acceptance

Checkpoint 3B3-B2-D is production-accepted on the Raspberry Pi. Acceptance completed on 2026-10-13 after external-generation metadata activation, a controlled new-path-only READY-package refresh, natural rolling replenishment, and viewer-confirmed YouTube moving video with audible audio.

This record supersedes the earlier candidate-only activation plan. This documentation update does not require a runtime rebuild or redeployment.

## Accepted production architecture

- Production source media, normalized library media, technical manifests, and the READY inbox remain on `/srv/nzyte-tv/media`. The Operations helper does not receive broad exFAT write authority.
- External programming metadata is stored on ext4 at `/var/lib/nzyte-tv-media-metadata`.
- The one-time adjacent-sidecar bootstrap created immutable generation `000000000001`, revision `1`. Bootstrap is not a dashboard or routine runtime operation and must not be rerun.
- `u24` and `nzyte-ops` have the required metadata access. `nzyte-dashboard` does not.
- The broadcaster reads pinned external generations through read-only access to the metadata root.
- Dashboard Media Operations uses the accepted local Dashboard-to-Operations Unix-socket boundary.
- The accepted runtime hotfix commit is `71e7991297391e1bbd22b7376ce7546c57f55f39`, which fixes vertical-layout package validation.

## Controlled production acceptance evidence

- The controlled acceptance package represented the `I See You` animated visual and used a new media path.
- One READY package was observed and accepted.
- One source asset and one library asset were added.
- Two metadata records were created and 464 existing records were preserved.
- One asset became newly eligible.
- Unresolved, incomplete, skipped, warning, and error counts were all zero.
- The metadata pointer advanced from generation `000000000001` / revision `1` to generation `000000000002` / revision `2`.
- Pre-refresh committed block 33 remained frozen on generation `000000000001` / revision `1`.
- Natural replenishment generated block 34 on generation `000000000002` / revision `2`; no committed block was rewritten.
- The new `I See You` asset was naturally selected into block 34.
- Public YouTube playback was operator-verified with moving video and audible audio.
- The controlled refresh did not restart the broadcaster, invoke FFmpeg normalization, rewrite active or committed programming, or alter Spotlight.

## Routine operation after acceptance

For later media additions, transfer all declared package members first and publish the final `*.ready.json` marker last. `Update Media Library` validates completed READY packages and may publish a new immutable external metadata generation. A routine refresh does not require a broadcaster restart.

Existing active blocks, committed blocks, and durable generation intents remain immutable. A newly published metadata generation affects only a later planning snapshot whose inputs have not already been captured.

Public moving video and audible audio remain separate operator acceptance evidence when viewer-facing verification is required.

## Compatibility and rollback

Adjacent `.nzytetv.meta.json` files remain required rollback and recovery evidence. Frozen snapshots with null metadata generation and revision permanently use adjacent authority. Do not remove those sidecars merely because external-generation mode is active.

Retain immutable metadata generations, operation records, and accepted READY evidence. Older accepted generations remain necessary for frozen planning snapshots and rollback diagnosis.

The pre-hotfix Operations candidate remains preserved at `/opt/nzyte-tv/releases/operations-3b3-b2d-0f9a35b-candidate`. The accepted hotfix candidate remains at `/opt/nzyte-tv/releases/operations-3b3-b2d-71e7991-candidate`.

If rollback is required, preserve the external metadata store and media evidence, restore the previously accepted application/configuration boundary through the reviewed handoff procedure, and verify local rolling state plus public moving video and audible audio. Never rewrite already committed blocks as part of rollback.

## Remaining deferred work

- Bootstrap remains intentionally unavailable through the dashboard and Operations HTTP surface.
- Automatic deletion or retention-policy cleanup of old metadata generations is not implemented.
- Accepted READY evidence is not automatically deleted after import.
- The Operations helper does not receive general source/library media write authority.

These are deliberate boundaries, not acceptance failures.
