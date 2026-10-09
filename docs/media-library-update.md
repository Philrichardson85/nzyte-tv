# Media-library update architecture

Checkpoint 3B3-B2 is being delivered in isolated slices. B2-A establishes only the programming-metadata storage and planning-read foundation described here. It does not add an Update Media Library button, package import, media refresh, deployment configuration, or Raspberry Pi migration.

## B2-A storage modes

Programming-metadata readers support two explicit modes:

- `Adjacent` is the default. A media file such as `library/Music Videos/Foo.mp4` continues to use `library/Music Videos/Foo.mp4.nzytetv.meta.json`. Existing workstation commands and v0.7-style composition therefore retain their current behavior.
- `ExternalGeneration` must be selected explicitly and requires an absolute metadata-store root. The application never selects it merely because a directory exists, and a missing or malformed external store does not fall back to adjacent sidecars.

B2-A provides programmatic storage options and dependency-injection points. It deliberately does not add production environment variables, command-line activation, systemd configuration, or a production metadata path. Those activation and migration decisions belong to a later deployment slice.

Existing metadata initialize, review, edit, rebind, and synchronize commands remain adjacent-sidecar writers. They do not write an external generation. Code using external mode must not invoke those commands as if they were external-store mutations; the future B2-B writer will construct and validate complete candidate generations instead.

## External generation layout

The storage foundation uses this layout beneath an explicitly supplied root:

```text
<metadata-root>/
  current.json
  generations/
    000000000001/
      generation.json
      source/
        Music Videos/Foo.mov.nzytetv.meta.json
      library/
        Music Videos/Foo.mp4.nzytetv.meta.json
```

Generation IDs are twelve-digit, nonzero decimal values. `current.json` contains schema version, positive revision, and generation ID. The referenced `generation.json` must contain the same revision and identity.

All media identities stored below `source/` and `library/` are normalized relative paths. Rooted paths, traversal segments, duplicate normalized identities, and symbolic-link/reparse escapes are rejected. Absolute storage paths are not part of durable planning identity.

Generation creation writes a private staging directory first and promotes it to the final `generations/<id>/` directory only after both metadata trees and `generation.json` are complete. Published generations are read as immutable. The low-level publication primitive validates the candidate manifest plus both required `source/` and `library/` trees, takes a cross-process current-pointer lock, checks the expected revision, and atomically replaces `current.json`. It never rewrites the generation. Old generations are retained. An interrupted creation may leave an unpublished staging directory, but that name cannot be selected by `current.json`.

## Planning snapshots

External-mode planning resolves `current.json` once and pins the selected generation for the complete inventory/readiness capture. A concurrent pointer publication cannot redirect that in-progress snapshot. The snapshot records the external generation ID and metadata revision, while adjacent snapshots omit those optional fields and retain the v0.7 serialized shape.

Readiness hashes use the programming-metadata JSON bytes from the pinned generation. They do not include the absolute metadata root, so identical metadata produces the same readiness identity regardless of mount location. Retry and committed-block verification reopen the exact generation recorded in the frozen input snapshot rather than consulting the then-current pointer.

Existing v0.7 planning snapshots and durable intents remain readable because the new identity fields are optional. Both fields being absent has a defined frozen meaning: adjacent sidecars remain the metadata authority for that snapshot, even if the process is later configured for external-generation mode and `current.json` advances. Verification never binds a legacy snapshot to the current external generation. Production migration must therefore preserve the adjacent sidecars needed to recover active blocks, committed blocks, and durable intents created before cutover; removing them would correctly fail readiness verification rather than fall back to unrelated external metadata.

Snapshots created in external mode record both generation ID and metadata revision. Their retries and committed-block verification reopen that exact immutable generation rather than consulting the then-current pointer. Committed-block schemas, planner lineage, rolling coordinator behavior, FFmpeg, and broadcaster behavior are unchanged.

## Deferred work

B2-A does not implement:

- Windows READY-package creation;
- package validation or bounded package hashing;
- external metadata-generation orchestration;
- media-library refresh operations;
- dashboard API or UI controls;
- production filesystem permissions or service changes;
- migration of current adjacent sidecars; or
- cleanup of old generations.

Source media, normalized MP4s, and technical `.nzytetv.json` manifests remain on the media drive. No B2-A component writes, deletes, normalizes, or transcodes those files.
