# V1 programming policy

Checkpoint 3A adds an optional, operator-managed programming policy to the existing deterministic playlist generator. Its goal is a deliberately paced, Nzyte-only classic MTV/BET-style music television channel rather than a random list of videos.

This checkpoint still generates finite playlist JSON files on demand. It does not generate another block while the station is running, append to the active station queue, create named shows or dayparts, or expose any viewer-facing graphics.

## Identity remains authoritative

NZYTE TV continues to use the existing two-level identity model:

- `contentGroupId` identifies the underlying song or song family.
- `assetId` identifies one exact video presentation.

For example, an official video, lyric video, visualizer, animated visual, performance, and short-form clip can have separate `assetId` values while sharing one `contentGroupId`. Programming policy does not introduce another song identity.

## Portable configuration

The preferred portable location is:

```text
<media-root>/catalog/programming.json
```

For the portable production drive mounted at `/srv/nzyte-tv/media`, that is:

```text
/srv/nzyte-tv/media/catalog/programming.json
```

When the catalog is intentionally kept under `/srv/nzyte-tv/catalog`, use `/srv/nzyte-tv` as the programming media root; its `library` convenience symlink and `catalog` directory form the same expected layout.

`build-playlist` automatically looks for `programming.json` beside the `song-catalog.json` supplied through `--catalog`. No extra build option is required.

The configuration is versioned, sparse, non-secret JSON. It never contains an RTMP/RTMPS destination, stream key, OAuth credential, process command line, station PID, runtime state, or resume cursor. Runtime state remains under the station state location, not in the media catalog.

Schema version 1 has this shape:

```json
{
  "schemaVersion": 1,
  "revision": 1,
  "repetition": {
    "exactAssetCooldownMinutes": 120,
    "sameContentGroupLookback": 2
  },
  "stationImaging": {
    "minimumSubstantialPieces": 3,
    "maximumSubstantialPieces": 5
  },
  "promoCadence": {
    "minimumIntervalMinutes": 30,
    "maximumIntervalMinutes": 45
  },
  "releaseAgeHotRotationEnabled": false,
  "personalities": [
    {
      "name": "music-heavy",
      "lanes": [
        "full-music",
        "full-music",
        "short-performance",
        "full-music",
        "personality",
        "full-music"
      ]
    },
    {
      "name": "mixed",
      "lanes": [
        "full-music",
        "short-performance",
        "full-music",
        "personality",
        "short-performance",
        "full-music"
      ]
    },
    {
      "name": "fast-paced",
      "lanes": [
        "short-performance",
        "full-music",
        "short-performance",
        "full-music",
        "short-performance",
        "personality-or-full-music"
      ]
    }
  ],
  "activeCampaign": {
    "enabled": false,
    "contentGroupId": null,
    "weightMultiplier": 2.0
  },
  "assetOverrides": {}
}
```

Writes use the same crash-safe approach as station state: write and flush a temporary file, then atomically move it over the live configuration. `programming init` never replaces an existing file. Mutations increment `revision` only when the effective configuration changes.

## Initialize, validate, and inspect

Initialize defaults once:

```bash
/opt/nzyte-tv/app/nzytetv programming init \
  --media-root /srv/nzyte-tv/media
```

Validate configuration, catalog references, overrides, and current library identities:

```bash
/opt/nzyte-tv/app/nzytetv programming validate \
  --media-root /srv/nzyte-tv/media
```

Inspect the operator-facing policy summary:

```bash
/opt/nzyte-tv/app/nzytetv programming status \
  --media-root /srv/nzyte-tv/media
```

Status reports schema/revision, campaign, override counts, repetition, bumper and promo cadence, the three internal personalities, and `Scheduler integration: ACTIVE`. It also says `Rolling future blocks: NOT IMPLEMENTED` so a valid policy is not mistaken for dynamic 24/7 queue generation.

## Default-on eligibility and editorial controls

Programming configuration does not register every asset. A newly normalized, metadata-resolved, technically playlist-eligible asset participates in future playlist generation automatically unless its sparse editorial override says Do Not Air:

```text
final eligibility = existing technical/metadata eligibility AND NOT doNotAir
```

Do Not Air changes only future programming selection. It does not move, delete, normalize, re-encode, or edit the media, technical manifest, source master, catalog identity, or `.nzytetv.meta.json` sidecar.

Set one or both editorial values:

```bash
/opt/nzyte-tv/app/nzytetv programming asset set free-fallin-video \
  --media-root /srv/nzyte-tv/media \
  --do-not-air true

/opt/nzyte-tv/app/nzytetv programming asset set free-fallin-visualizer \
  --media-root /srv/nzyte-tv/media \
  --weight 1.5
```

Clear Do Not Air while retaining any custom weight:

```bash
/opt/nzyte-tv/app/nzytetv programming asset set free-fallin-video \
  --media-root /srv/nzyte-tv/media \
  --do-not-air false
```

Return an asset completely to defaults:

```bash
/opt/nzyte-tv/app/nzytetv programming asset reset free-fallin-video \
  --media-root /srv/nzyte-tv/media
```

Defaults are `doNotAir=false` and `weightMultiplier=1.0`. Multipliers must be finite and between `0.1` and `10.0`; zero, negative, NaN, infinity, and excessive values are rejected. Unknown `assetId` references fail validation rather than being guessed.

## Active Campaign / Spotlight Record

V1 supports one active campaign targeting one catalog `contentGroupId`. Set it with the default 2.0x song-family multiplier:

```bash
/opt/nzyte-tv/app/nzytetv programming campaign set free-fallin \
  --media-root /srv/nzyte-tv/media
```

Or specify a validated custom multiplier:

```bash
/opt/nzyte-tv/app/nzytetv programming campaign set free-fallin \
  --media-root /srv/nzyte-tv/media \
  --weight 2.5
```

Clear it:

```bash
/opt/nzyte-tv/app/nzytetv programming campaign clear \
  --media-root /srv/nzyte-tv/media
```

The multiplier belongs to the song family, not every presentation. Twelve eligible visuals for Song A therefore do not give Song A twelve baseline lottery entries against a song with one visual. The scheduler selects a content group first with one family weight, applies the campaign multiplier there, then selects an eligible presentation within that family. An asset-level multiplier affects that presentation choice. Do Not Air and hard song-family adjacency are applied before weighting, so a campaign cannot bypass them.

The previous release-age hot-rotation boost is a separate mechanism. With programming policy active it defaults to OFF, preventing an automatic recent-release boost from silently multiplying the intentional campaign boost. It can be explicitly enabled with `releaseAgeHotRotationEnabled`, in which case the operator has deliberately enabled both policies. When `programming.json` is absent, legacy age-based behavior remains unchanged.

## Repetition policy

The active V1 policy separates three concerns:

1. **Exact asset:** the same `assetId` has a configurable preferred cooldown, initially 120 minutes. Existing controlled relaxation still permits progress when inventory cannot satisfy it.
2. **Song-family adjacency:** two adjacent substantial programming pieces never use the same `contentGroupId` while any valid alternative exists.
3. **Song-family cluster:** the scheduler strongly prefers a group absent from the previous configurable number of substantial pieces, initially two. This is a soft preference so limited inventories remain schedulable.

An unavoidable pathological inventory can report an adjacency violation rather than deadlock, but campaign weight never causes one while another legal group exists.

Substantial pieces are determined from existing types:

```text
music-video
lyric-video
visualizer
animated-visual
performance
short-form
vlog
special
```

These insertion types do not separate two appearances of the same song family:

```text
bumper
promo
interstitial
advertisement
```

Thus `Free Fallin video -> bumper -> Free Fallin visualizer` is still adjacent same-song programming and is rejected when another song is available. No redundant per-asset “substantial” flag is stored.

## Internal programming personalities

The three internal personalities are sequencing texture, not viewer-facing shows:

- `music-heavy` favors full music presentations.
- `mixed` alternates full music, shorter/performance material, and personality content.
- `fast-paced` favors shorter/performance material while retaining full music and personality opportunities.

Their lane templates live in `programming.json`, so operators can tune the pattern without recompiling. The scheduler chooses patterns deterministically for a fixed configuration, history, seed, catalog, and library and does not immediately repeat the same personality. A lane is a preference, not a promise: inventory and safety rules remain authoritative, and missing lane inventory falls back through the existing controlled scheduling path.

Full versus short song presentation reuses the existing central 60-second duration rule. `short-form` is always short; other song presentations at or below 60 seconds are short. There is no dedicated Shorts block.

Vlogs are personality programming, not music. Imaging or commercial insertions do not hide an otherwise consecutive vlog run, and a non-vlog substantial alternative is preferred under normal inventory conditions. No named vlog show or daypart exists in V1.

## Bumpers, promos, and commercials

Existing `bumper` assets serve as station IDs. With the initial configuration, one is preferred after three substantial pieces and due by five. Available bumpers rotate by least-recently-used opportunity before repeating, and bumper-to-bumper scheduling is prevented by the cadence minimum.

`promo` and `advertisement` share the short mini-break cadence. They are ineligible before 30 minutes, preferred through 45 minutes, and due afterward. A promo or advertisement resets the shared interval, so normal programming does not create promo-to-promo pods. Existing interstitial cadence remains compatible and separate.

## Scheduling order

Checkpoint 3A extends the existing scheduler rather than creating a second one. Conceptually each selection follows this order:

1. Honor an overdue bumper, promo/advertisement, or interstitial insertion.
2. Ask the current internal pattern for a lane.
3. Use configured and availability-adjusted category airtime targets to prefer a type.
4. Apply exact-asset protection, hard substantial-item adjacency, soft song-family lookback, and vlog protection.
5. Select a song family once, applying an optional explicit release-age weight and the one active campaign multiplier.
6. Select an eligible presentation inside that family using sparse asset weight.
7. Use the existing deterministic controlled fallback ladder when inventory cannot satisfy a preference.

Playlist schema remains version 1. Additive summary diagnostics identify whether programming policy was active and count lane fallbacks, song-cluster relaxations, and unavoidable adjacency violations. Generated files retain everything required by the existing broadcaster.

## Backward compatibility and history

An existing media/catalog root without `programming.json` remains valid. `build-playlist` uses the accepted legacy scheduler behavior, including legacy release-age and time-based same-song pacing. `media init` deliberately does not create `programming.json`; policy activation is the explicit `programming init` action.

Existing generated playlist JSON remains valid. The broadcaster, station queue identity, durable schema-v2 resume state, FFmpeg recovery, and systemd behavior are unchanged.

`playlists/history.json` remains planned scheduling history. It carries scheduled asset/group starts across generated playlist boundaries; it is not rewritten into an actual air log and is not merged with station runtime state. Checkpoint 3A does not record what actually aired.

## Current scope boundary

Checkpoint 3A does not implement:

- rolling automatic future-block generation;
- dynamic station queue append or directory watching;
- actual air-history logging;
- Skip or Force Play controls;
- a Web UI;
- named shows or dayparts;
- Power/Current/Recurrent/Gold tiers;
- album/project or multi-artist programming;
- guest artists;
- viewer-facing NOW/NEXT, bugs, or dynamic graphics;
- YouTube API monitoring or alerts; or
- archive preservation.

Generate and inspect finite six-hour blocks with the existing `build-playlist` command. A later checkpoint may call the same programming services from a lightweight Program Director UI or rolling queue generator; neither is created here.
