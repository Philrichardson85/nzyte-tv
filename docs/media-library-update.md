# Media-library update architecture

Checkpoint 3B3-B2 is being delivered in isolated slices. B2-A established the programming-metadata storage and planning-read foundation. B2-B added local READY-package preparation, bounded package verification, external-generation refresh, and adjacent-sidecar bootstrap tooling. B2-C adds a default-disabled Operations-helper application surface and dashboard UI for invoking only the existing refresh operation. Production configuration, filesystem permissions, external-mode activation, bootstrap, and Raspberry Pi deployment/migration remain deferred to B2-D.

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
      inventory.json
      source/
        Music Videos/Foo.mov.nzytetv.meta.json
      library/
        Music Videos/Foo.mp4.nzytetv.meta.json
  operations/
    <operation-id>.json
```

Generation IDs are twelve-digit, nonzero decimal values. `current.json` contains schema version, positive revision, and generation ID. The referenced `generation.json` must contain the same revision and identity.

All media identities stored below `source/` and `library/` are normalized relative paths. Rooted paths, traversal segments, duplicate normalized identities, and symbolic-link/reparse escapes are rejected. Absolute storage paths are not part of durable planning identity.

Generation creation writes a private staging directory first and promotes it to the final `generations/<id>/` directory only after both metadata trees and `generation.json` are complete. Published generations are read as immutable. The low-level publication primitive validates the candidate manifest plus both required `source/` and `library/` trees, takes a cross-process current-pointer lock, checks the expected revision, and atomically replaces `current.json`. It never rewrites the generation. Old generations are retained. An interrupted creation may leave an unpublished staging directory, but that name cannot be selected by `current.json`.

## Planning snapshots

External-mode planning resolves `current.json` once and pins the selected generation for the complete inventory/readiness capture. A concurrent pointer publication cannot redirect that in-progress snapshot. The snapshot records the external generation ID and metadata revision, while adjacent snapshots omit those optional fields and retain the v0.7 serialized shape.

Readiness hashes use the programming-metadata JSON bytes from the pinned generation. They do not include the absolute metadata root, so identical metadata produces the same readiness identity regardless of mount location. Retry and committed-block verification reopen the exact generation recorded in the frozen input snapshot rather than consulting the then-current pointer.

Existing v0.7 planning snapshots and durable intents remain readable because the new identity fields are optional. Both fields being absent has a defined frozen meaning: adjacent sidecars remain the metadata authority for that snapshot, even if the process is later configured for external-generation mode and `current.json` advances. Verification never binds a legacy snapshot to the current external generation. Production migration must therefore preserve the adjacent sidecars needed to recover active blocks, committed blocks, and durable intents created before cutover; removing them would correctly fail readiness verification rather than fall back to unrelated external metadata.

Snapshots created in external mode record both generation ID and metadata revision. Their retries and committed-block verification reopen that exact immutable generation rather than consulting the then-current pointer. Committed-block schemas, planner lineage, rolling coordinator behavior, FFmpeg, and broadcaster behavior are unchanged.

## B2-B READY packages

A READY package is a fixed, strict schema for one source asset and its derived library asset. It declares only relative source/library identities and the length and SHA-256 digest of the source media, normalized MP4, technical `.nzytetv.json` manifest, and any present source/library programming sidecars. It contains no arbitrary file list, absolute root, command, or environment value.

Prepare one package on the Windows normalization workstation only after normalization and metadata work are complete:

```powershell
nzytetv.exe media package prepare `
  --media-root "E:\" `
  --source-relative "Music Videos\Artist - Record.mov"
```

The command derives the library path through the normal library-path policy, validates the existing technical relationship, validates optional programming sidecars, hashes only those members sequentially, and atomically publishes `<package-id>.ready.json` under `<media-root>\inbox` (or the explicitly supplied `--inbox-root`). It does not invoke FFmpeg, normalize, repair, or modify source/library media. A pre-existing final READY filename is not overwritten.

When transferring a package, preserve its relative paths and copy the source member, normalized MP4, technical manifest, and declared programming sidecars first. Copy/publish the final `*.ready.json` file into the trusted inbox last. Temporary files, partial files, unrelated files, and media without a final READY marker are ignored. There is no stable-size or age heuristic.

Technical manifests retain the existing source fingerprint rules, including source-relative identity, size, last-modified UTC, schema, and broadcast profile. A transfer mechanism that changes required timestamps can therefore cause conservative package rejection; this must be verified during later Pi acceptance rather than weakened in code.

## B2-B refresh and inventory

The local administrative refresh command is:

```text
nzytetv media metadata refresh --media-root <media-root> --metadata-root <external-metadata-root> [--inbox-root <path>]
```

Refresh requires an already-bootstrapped external store. It takes one non-queuing cross-process writer lock, pins the current immutable generation, reads only final READY manifests from the configured inbox, verifies each declared member sequentially, and constructs a complete candidate generation in staging. Publication is staging-to-final promotion followed by expected-revision publication of `current.json`. Readers do not take the writer lock.

The immutable `inventory.json` carries accepted package IDs/digests, relative source/library identities, member evidence, metadata evidence, asset IDs, resolved content groups, and a fixed readiness classification. The inventory contains no absolute storage root. A package is accepted only for source and library identities that are new to the accepted inventory:

- the same package ID and manifest digest is `alreadyProcessed`;
- the same package ID with a different digest is rejected;
- a new package ID targeting an accepted media identity is rejected; and
- a run with no newly acceptable packages does not create a generation or increment the metadata revision.

Refresh never writes, replaces, repairs, moves, or deletes source files, library MP4s, technical manifests, READY evidence, or adjacent programming sidecars. It writes only lock/staging/generation/inventory/operation records below the explicitly configured external metadata root. It does not call FFmpeg/FFprobe, rolling planning, station control, or systemd.

Known songs must resolve conservatively to the existing song catalog. Ambiguous and unknown songs remain unprocessed so a later refresh can accept them after separate trusted catalog maintenance. B2-B does not create catalog entries, edit programming policy, or change Spotlight. Deterministic non-song metadata may be created directly in the candidate external generation.

Operation records contain fixed status/issue codes and safe relative identities, not raw exceptions or absolute paths. `current.json` plus immutable generations remain authoritative. A crash before pointer publication leaves the prior generation current; a promoted but unpublished generation is an inert orphan; a crash after pointer publication leaves the new generation authoritative, and the next serialized invocation can reconcile its running operation record. Refresh never retries itself automatically.

## Explicit adjacent-sidecar bootstrap

Bootstrap is a separate administrative operation; runtime external mode still fails closed when `current.json` is missing or malformed.

Preview first:

```text
nzytetv media metadata bootstrap --media-root <media-root> --metadata-root <external-metadata-root>
```

Publish only after reviewing a clean preview:

```text
nzytetv media metadata bootstrap --media-root <media-root> --metadata-root <external-metadata-root> --publish
```

Bootstrap requires no existing `current.json`, takes the same refresh-writer lock, scans and strictly validates paired adjacent source/library metadata, validates media and technical-manifest relationships and catalog resolution, preserves valid JSON bytes in their corresponding external trees, and creates generation/revision 1. Malformed metadata, missing pairs, duplicate asset IDs, unresolved catalog relationships, or other blocking inconsistencies prevent publication. Re-running bootstrap after `current.json` exists is refused.

Bootstrap never changes adjacent sidecars or media. Those adjacent sidecars must remain available during production migration because v0.7 active/committed blocks and durable intents with null metadata-generation identity explicitly retain adjacent authority.

## B2-C application surface

B2-C adds three allowlisted Operations-helper routes over the existing Unix-domain socket:

- `GET /api/v1/media-library` returns a cheap, sanitized feature/generation summary;
- `POST /api/v1/media-library/refresh` reserves the existing cross-process refresh lock and accepts an asynchronous refresh only when the caller's expected metadata revision is current; and
- `GET /api/v1/media-library/operations/{operationId}` returns a sanitized durable operation result.

The feature is explicitly disabled by default. Enabling it later requires trusted Operations configuration for the media root, inbox root, and external metadata root. The external writable root must not overlap the read-only media root. Browser requests cannot provide or override those paths. Missing or invalid enabled configuration fails the media feature closed while Spotlight and the read-only station dashboard remain available.

The helper returns `202 Accepted` only after it owns the B2-B refresh-writer lease, so a concurrent trusted CLI refresh cannot be accepted falsely. One host-owned background worker runs the operation independently of the browser request. There is no queue, automatic retry, or startup refresh. Durable B2-B operation records allow polling and interruption reconciliation without making in-memory state authoritative.

The dashboard POST is protected by ASP.NET Core antiforgery validation, strict JSON, a 1 KiB body limit, and an expected metadata revision. The UI requires confirmation, polls one operation at a time, and renders only allowlisted counts and fixed-code explanations through safe text assignment. It does not receive media paths, raw exceptions, or storage configuration.

Bootstrap remains deliberately absent from both Operations and dashboard routes. It is a trusted one-time CLI/admin action for B2-D. Refresh does not normalize, invoke FFmpeg, write the media drive, restart the broadcaster, rewrite active/committed blocks, or promise selection into an immediate block. Publication affects the first newly captured planning snapshot; frozen durable intents retain their original metadata authority.

## Deferred work

B2-C does not implement:

- production filesystem permissions or service changes;
- production feature configuration or external-mode activation;
- web-accessible bootstrap or migration;
- executed production migration/activation; or
- cleanup of old generations.

Source media, normalized MP4s, and technical `.nzytetv.json` manifests remain on the media drive. No B2-A component writes, deletes, normalizes, or transcodes those files.
