# Content catalog and asset metadata

The catalog and programming sidecars provide stable content identity for playlist scheduling. Metadata commands do not generate playlists, broadcast, or encode media. For the complete novice operating sequence, see the [operations runbook](operations-runbook.md).

The governing rule is:

> Encoding is independent from metadata resolution.

A source may normalize and verify successfully while its programming metadata is missing, ambiguous, or unresolved. Metadata commands do not locate or invoke FFmpeg, do not alter video or audio, and never require a production-library re-encode.

## Production location

The recommended catalog directory is:

```text
/srv/nzyte-tv/catalog/
`-- song-catalog.json
```

The location is an operational convention, not a domain constant. Every command accepts the source, library, and catalog paths explicitly.

## Two identities

`contentGroupId` identifies a song or recording as a programming concept. Every visual presentation of the same recording shares it:

```text
free-fallin
```

`assetId` identifies one specific video presentation:

```text
free-fallin-music-video
free-fallin-lyric-video
free-fallin-performance-krog-street
free-fallin-visualizer
free-fallin-animated-visual
```

Both IDs are stable. Filenames help discover relationships during initialization, but metadata becomes authoritative afterward. Once a song relationship resolves, the catalog is authoritative for canonical song `title` and `artist`; `assetId`, type/subtype, and other programming fields remain asset metadata. Renaming a file must not create a new identity for an initialized asset.

Alternate visuals of the same recording—music videos, lyric videos, visualizers, animated visuals, performances, and short-form clips—share one `contentGroupId`. A substantially different recording or remix may have its own `contentGroupId`; that is an editorial catalog decision and is never inferred automatically from a filename. Non-song programming such as vlogs, bumpers, album teasers, montages, medleys, promos, advertisements, and specials does not receive a fake song group. Multi-song relationships are not modeled, so those assets may keep `contentGroupId: null`.

## Master song catalog schema

The catalog is versioned JSON. Schema version 1 is:

```json
{
  "schemaVersion": 1,
  "songs": [
    {
      "contentGroupId": "free-fallin",
      "title": "Free Fallin",
      "artist": "Nzyte",
      "project": "American Dreams 2",
      "aliases": [
        "Free Falling"
      ]
    }
  ]
}
```

`contentGroupId`, `title`, and `artist` are required. `contentGroupId` must be unique. `project` and `aliases` are optional.

Song titles are not globally unique. These entries are valid together:

```json
{
  "contentGroupId": "cold-american-dreams-2",
  "title": "Cold",
  "artist": "Nzyte",
  "project": "American Dreams 2"
}
```

```json
{
  "contentGroupId": "cold-future-project",
  "title": "Cold",
  "artist": "Nzyte",
  "project": "Future Project"
}
```

Malformed JSON, missing required values, duplicate `contentGroupId` values, and unsupported schema versions fail with validation errors before asset metadata is changed.

Aliases retain the official title as the canonical catalog value. The real filename spelling `I Did It 4 U` is represented as:

```json
{
  "contentGroupId": "i-did-it-for-you",
  "title": "I Did It For You",
  "artist": "Nzyte",
  "aliases": [
    "I Did It 4 U"
  ]
}
```

Aliases may also be known acronyms, such as `WALTW`. Exact normalized aliases resolve to the official song; the alias never replaces the canonical `title`.

## Programming metadata schema

Programming metadata lives beside a source master:

```text
Free Fallin - Krog Street Performance.mp4
Free Fallin - Krog Street Performance.mp4.nzytetv.meta.json
```

Schema version 1 is:

```json
{
  "schemaVersion": 1,
  "assetId": "free-fallin-performance-krog-street",
  "contentGroupId": "free-fallin",
  "title": "Free Fallin",
  "artist": "Nzyte",
  "type": "performance",
  "subtype": null,
  "rotationStartDate": "2026-09-27",
  "enabled": true,
  "seriesId": null,
  "episodeNumber": null,
  "tags": []
}
```

`rotationStartDate` is explicit programming data. NZYTE TV does not invent it from filesystem timestamps. An unresolved song asset has `contentGroupId: null`. `subtype` is optional and extensible; it is not a closed enum.

A short-form asset can use:

```json
{
  "schemaVersion": 1,
  "assetId": "cash-rules-pov-01",
  "contentGroupId": "cash-rules",
  "title": "Cash Rules",
  "artist": "Nzyte",
  "type": "short-form",
  "subtype": "pov",
  "rotationStartDate": null,
  "enabled": true,
  "seriesId": null,
  "episodeNumber": null,
  "tags": []
}
```

Known initialization subtypes currently include `pov`, `lipsync`, `stock`, `mic-drop`, `behind-the-scenes`, `thought`, `meme`, and `ai-visual`. Editors may use future subtype values without a schema change.

The programming suffix is distinct from normalization state:

| Sidecar | Owner and purpose |
|---|---|
| `.nzytetv.meta.json` | User-facing programming identity and catalog metadata |
| `.nzytetv.json` | Machine-managed source fingerprint and broadcast-profile state |

Do not edit or substitute one for the other.

Supported programming types are:

```text
music-video
lyric-video
performance
visualizer
animated-visual
short-form
vlog
bumper
promo
interstitial
advertisement
special
```

Directory-to-type mappings are centralized in the application. `Visualizers` maps to `visualizer`, and `Animated Visuals` maps to `animated-visual`; both require song resolution just like music videos, lyric videos, performances, and short-form song content. The remaining directory-backed mappings include `Music Videos`, `Lyric Videos`, `Performance Videos`, `Vlog Episodes`, `Bumpers`, `Promos`, `Interstitials`, `Advertisements`, and `Specials`. `short-form` remains supported without a dedicated directory created by `media init`. A recognized short-form descriptor prefix may refine a new song/video or vlog asset to `short-form`; it does not override explicit existing metadata or non-song promo/special categories.

`Performance Videos` remains top-level type `performance`. Filename descriptors such as `Lipsync`, `Lip Sync`, `MicDrop`, and `Mic Drop` may initialize the subtype as `lipsync` or `mic-drop`; they do not replace the top-level type with `short-form`. Existing explicit metadata remains authoritative.

A visualizer is a full-song static or lightly animated graphical presentation. An animated visual is an animated, narrative, cinematic, anime/movie-style, AI-animated, or similar full-song/extended song presentation. The folder selects the programming type, not the song identity: catalog matching must still resolve the filename or alias, and ambiguous or unresolved assets remain subject to metadata review.

## Initialize metadata

Preview a run first:

```bash
/opt/nzyte-tv/app/nzytetv metadata initialize \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library \
  --catalog /srv/nzyte-tv/catalog/song-catalog.json \
  --dry-run
```

`--dry-run` writes nothing.

Apply initialization:

```bash
/opt/nzyte-tv/app/nzytetv metadata initialize \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library \
  --catalog /srv/nzyte-tv/catalog/song-catalog.json
```

Initialization:

- scans only supported media extensions;
- detects type from the source category and recognized short-form descriptor prefixes;
- preserves valid existing metadata and all prior human decisions;
- generates a readable, unique `assetId` for a new asset;
- attempts conservative song matching;
- writes source metadata atomically;
- copies programming metadata to the corresponding normalized library asset when that asset exists;
- reports resolved groups, assets needing review, unresolved assets, errors, and orphaned sidecars;
- reports encoding, metadata, and playlist-eligibility status.

Repeated initialization is safe. A valid resolved relationship is not inferred again; initialization reconciles its `title` and `artist` to the referenced catalog entry while preserving `assetId`, type/subtype, and other programming fields. A valid unresolved sidecar may gain a relationship later when a catalog change makes exactly one match possible; its canonical title and artist then come from the catalog without changing its asset identity.

Missing source assets are reported without deleting their metadata or history.

Initialization groups resolved assets visibly and includes short-form subtype when known:

```text
GROUP: cash-rules

    [music-video]
    Music Videos/Nzyte - CASH RULES (Official Music Video).mp4

    [lyric-video]
    Lyric Videos/Nzyte - Cash Rules (Lyric Video).mp4

    [short-form / pov]
    Vlog Episodes/Content Template POV1-Cash Rules.mp4

    [short-form / mic-drop]
    Vlog Episodes/Content Template MicDrop2 - Cash Rules.mp4
```

Review entries include the matcher reason and all remaining plausible catalog candidates, including duplicate acronyms and duplicate canonical titles.

## Matching behavior

For song-based types, matching uses normalized, token-boundary-aware title sequences in the filename. Comparisons ignore capitalization, extra whitespace, hyphen spacing, common apostrophe variants, punctuation, and the extension. Recognized noise includes `Content Template`, numbered `POV`, `Stock`, `MicDrop`, `Lipsync`, `Thought`, `Meme`, and `AI` prefixes, `BTS`, numbered `Content` suffixes, `Official Video`, `Official Music Video`, `Lyric Video`, `Performance`, and `Visualizer`. Arbitrary words are not stripped.

The order is:

1. Preserve an existing valid explicit relationship.
2. Auto-resolve one exact normalized canonical-title match.
3. If there is no canonical match, auto-resolve one exact normalized alias match.
4. Consider a canonical title or alias that occurs as a distinct, boundary-safe sequence within a descriptor-heavy filename.
5. Send genuinely plausible multiple matches to review.
6. Leave no-match assets unresolved.

There is no silent fuzzy assignment. For overlapping titles, the longer exact segment is preferred: `Cold Heart - Performance.mp4` resolves to `Cold Heart`, not `Cold`. Duplicate `Cold` entries still go to review, where their `project` and IDs distinguish them. Short aliases such as `AD`, `NG`, and `TM` match only complete filename tokens, never characters embedded in an unrelated word. If the same alias or acronym belongs to multiple catalog entries, review is required and the report explains the ambiguity.

Examples such as `Content Template POV1-Cash Rules.mp4`, `Content Template Stock10 - Cash Rules.mp4`, `Content Template MicDrop2 - Cash Rules.mp4`, and `BTS Cash Rules.mp4` detect `Cash Rules` as the canonical song title while retaining the descriptor for subtype and asset identity.

## Review unresolved assets

Run:

```bash
/opt/nzyte-tv/app/nzytetv metadata review \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library \
  --catalog /srv/nzyte-tv/catalog/song-catalog.json
```

Review prompts only for song assets whose relationship is unresolved or references a group no longer present in the catalog. The user chooses from catalog candidates or leaves the asset unresolved. A confirmed relationship becomes authoritative. Review preserves `assetId`, type/subtype, and non-catalog programming fields; it writes the selected catalog entry's canonical `title` and `artist`, updates the source sidecar, and synchronizes the library sidecar when the normalized asset exists.

## Synchronize without encoding

To propagate programming-only changes:

```bash
/opt/nzyte-tv/app/nzytetv metadata sync \
  /srv/nzyte-tv/source \
  /srv/nzyte-tv/library
```

Synchronization copies valid source programming metadata only when the corresponding normalized library video exists. Missing library videos are reported and are not normalized automatically. Changes to `enabled`, `title`, `type`, `subtype`, `contentGroupId`, `rotationStartDate`, `seriesId`, `episodeNumber`, or `tags` never invoke FFmpeg and never alter the technical normalization manifest.

If multiple source files would map to the same normalized `.mp4` path, synchronization reports an error for each source and writes no library sidecar for that destination.

Playlist generation and broadcasting consume playable assets and programming metadata from `/srv/nzyte-tv/library`, not `/srv/nzyte-tv/source`.

## Eligibility

Eligibility determines whether the current playlist generator may schedule an asset.

A song asset is eligible only when:

- its normalized library video exists;
- its `.nzytetv.json` technical manifest exists;
- its `.nzytetv.meta.json` programming metadata exists in the library;
- programming metadata is valid;
- `contentGroupId` resolves in the catalog;
- `enabled` is `true`.

Unresolved metadata does not affect encoding:

```text
Encoding status: READY
Metadata status: UNRESOLVED
Playlist eligibility: NO
```

After explicit resolution and synchronization:

```text
Encoding status: READY
Metadata status: RESOLVED
Playlist eligibility: YES
```

## Rename and rebind

After intentionally renaming a source, re-associate its sidecar explicitly:

```bash
/opt/nzyte-tv/app/nzytetv metadata rebind \
  "/srv/nzyte-tv/source/Music Videos/Old Name.mp4" \
  "/srv/nzyte-tv/source/Music Videos/New Name.mp4"
```

The old video path may already be absent, but its sidecar must exist and the new video must exist. Rebind refuses to overwrite metadata already associated with the new source. It moves only the programming sidecar, preserving `assetId`, `contentGroupId`, and user fields. Run `metadata sync` afterward when the corresponding normalized-library filename has also been reconciled.

## Override a wrong folder type

Folder category is a strong initialization default, not permanent truth. For example, correct a promo stored in `Vlog Episodes` without moving or encoding it:

```bash
/opt/nzyte-tv/app/nzytetv metadata edit \
  "/srv/nzyte-tv/source/Vlog Episodes/Season 2 promo.mp4" \
  --type promo
```

The edit preserves `assetId` and all unrelated fields. Changing to a non-song type clears `contentGroupId` so the sidecar remains structurally valid. A short-form or performance override may also pass an extensible subtype with `--subtype`; for example, `performance / lipsync` or `performance / mic-drop`. The command writes only the source `.nzytetv.meta.json`; run `metadata sync` to propagate it to an existing library asset. It does not move media, invoke FFmpeg, modify `.nzytetv.json`, or re-encode anything.

## Scope boundary

Metadata commands do not normalize, schedule, broadcast, watch the filesystem, call the YouTube API, or provide a GUI.

Most importantly: encoding never waits for metadata questions.
