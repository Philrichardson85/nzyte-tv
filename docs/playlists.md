# Playlist and programming engine

NZYTE TV generates deterministic, inspectable schedules from normalized library assets. Playlist generation does not broadcast, re-encode, edit metadata, or modify media. For the complete novice workflow around generation, history, and live playback, see the [operations runbook](operations-runbook.md).

## Build a playlist

```bash
/opt/nzyte-tv/app/nzytetv build-playlist \
  /srv/nzyte-tv/library \
  --catalog /srv/nzyte-tv/catalog/song-catalog.json \
  --output /srv/nzyte-tv/playlists/current.json \
  --duration 6h \
  --seed 20260927 \
  --history /srv/nzyte-tv/playlists/history.json
```

Paths are explicit CLI inputs and are not hardcoded in domain logic. `--duration` accepts positive hour, minute, or second values such as `6h`, `90m`, or `3600s`.

When `--seed` is omitted, the command uses a UTC daily seed and records it in the playlist. The same eligible snapshot, policy, history, generation time, and seed produce the same item ordering. Supplying a seed is recommended for acceptance tests and debugging.

Preview without writing either output:

```bash
/opt/nzyte-tv/app/nzytetv build-playlist \
  /srv/nzyte-tv/library \
  --catalog /srv/nzyte-tv/catalog/song-catalog.json \
  --output /srv/nzyte-tv/playlists/current.json \
  --duration 6h \
  --history /srv/nzyte-tv/playlists/history.json \
  --dry-run
```

Dry-run still takes a real read-only snapshot and inspects durations with FFprobe. It writes no playlist, history, metadata, manifest, or media file.

Checkpoint 3A automatically looks for `programming.json` beside the `song-catalog.json` supplied through `--catalog`. When present, the versioned V1 programming policy is validated and applied. When absent, the accepted legacy scheduler behavior remains available without migration. See [V1 programming policy](programming.md) for initialization and operator commands.

## Why six hours

Six hours is the initial production schedule target. The present library contains only about 1 hour 17 minutes of unique programming, so a six-hour schedule necessarily repeats material. The engine keeps adding complete assets until it reaches or slightly exceeds the target; it never cuts media merely to hit the boundary.

The summary reports target duration, actual duration, and overrun.

## Eligibility snapshot

Generation scans the normalized library once. An asset is schedulable only when the eligibility rules pass:

- the normalized media exists;
- the technical `.nzytetv.json` manifest exists;
- valid `.nzytetv.meta.json` programming metadata exists;
- the asset is enabled;
- song-based metadata has a valid catalog `contentGroupId`;
- FFprobe returns a positive duration;
- the active policy gives the asset type an airtime target or cadence.

With Checkpoint 3A policy active, sparse `assetOverrides` are applied after these existing technical and metadata checks. A Do Not Air override excludes the asset from future playlist generation. An asset with no override remains eligible by default; adding prepared content never requires registering it in `programming.json`.

Missing, disabled, invalid, unresolved, duration-less, duplicate-identity, or policy-disabled assets are excluded with visible reasons. Schedule generation never repairs or mutates them. Zero participating eligible assets is a clear failure.

`visualizer` represents a full-song static or lightly animated graphical presentation. `animated-visual` represents an animated, narrative, cinematic, anime/movie-style, AI-animated, or similar extended song presentation. Both are normal music programming, require a valid catalog relationship, and are never treated as promo, bumper, interstitial, advertisement, or vlog cadence content.

## Shared category policy and legacy behavior

The strongly typed base policy is centralized in `NzyteTv.Core`. The following table describes the compatibility behavior used when `programming.json` is absent. The active Checkpoint 3A overrides are described in the next section.

| Rule | Default |
|---|---:|
| Target duration | 6 hours |
| Music-video airtime | 20% |
| Lyric-video airtime | 15% |
| Visualizer airtime | 15% |
| Animated-visual airtime | 20% |
| Performance airtime | 10% |
| Short-form airtime | 5% |
| Vlog airtime | 15% |
| Exact asset cooldown | 2 hours |
| Same `contentGroupId` preferred cooldown | 90 minutes |
| Same `contentGroupId` normal floor | 60 minutes |
| Music-first rescue floor | 45 minutes, only before vlog #3+ |
| Consecutive vlogs | one preferred; two permitted as fallback |
| Bumper cadence | every 4–5 normal programs (legacy mode) |
| Interstitial cadence | every 20–30 minutes |
| Promo cadence | every 30–45 minutes |

Airtime targets use scheduled seconds, not item counts. Only targets represented by eligible inventory are normalized into the active mix. The default normal-program mix is 20% music-video, 15% lyric-video, 15% visualizer, 20% animated-visual, 10% performance, 5% short-form, and 15% vlog. Music/artist-oriented types therefore total 85%; short-form is ordinary eligible programming, while advertisement and special assets still require an explicit policy target. Bumpers, promos, and interstitials remain outside the normal-program target mix and participate only when eligible inventory exists; their absence never prevents generation.

Configured targets remain the desired station mix. For each generation, the scheduler also calculates availability-aware effective targets from eligible duration, the requested horizon, and the two-hour exact-asset preference. Practical capacity is the category's unique eligible duration multiplied by the normal appearance budget (`ceiling(horizon / exact-asset cooldown)`); this is target planning, not a hard replay limit. An unavailable category therefore has a zero effective target, while an inventory-limited category is not treated as capable of supplying impossible airtime.

Unavailable target airtime is redistributed by duration to music-oriented normal categories with remaining practical capacity before vlog. The scheduler uses these effective targets for category-deficit ranking without changing the configured percentages. The playlist summary and CLI show both target sets, capacity-limited categories, practical capacity by category in JSON, and redistributed target airtime. If fallback is necessary, scheduling may still exceed practical capacity and relax the exact-asset preference through the existing counted path.

For song pacing, short-form is always a short presentation. Other song-based normal programming is short when its measured duration is 60 seconds or less; longer assets are full presentations. Full-to-full repeats retain the 90-minute preferred, 60-minute normal floor, and 45-minute music-rescue behavior. Directional short pacing is short-to-short 15/10 minutes, full-to-short 30/20 minutes, and short-to-full 30/15 minutes (preferred/floor). Any necessary floor crossing is an explicit, separately reported emergency; exact-asset cooldowns continue to apply.

Normal category targets are airtime preferences. Once vlog is at or above its configured share, another legally eligible music-oriented normal program is preferred before additional vlog fallback, including when a configured music category such as short-form has no inventory. Eligible full presentations in under-target music categories also receive priority over short clips from categories already at target, and over same-song shorts that would reset their directional timer. These preferences never bypass exact-asset cooldown selection, song-spacing floors, hot-rotation restrictions, or cadence eligibility.

Candidate ranking also considers projected airtime after the candidate plays. A long vlog that would materially overshoot its target yields to legal music even when vlog is technically below target beforehand. When music is deficient, longer full presentations from under-target categories receive airtime-efficiency preference over lower-value short clips. Within otherwise comparable song candidates, the content group played least recently is preferred so short clips do not synchronize every group near the same cooldown boundary. These are ranking preferences only; they do not change or bypass any cooldown floor.

Cadence windows have a minimum eligibility boundary as well as a preferred/overdue boundary:

- a promo is ineligible before 30 minutes, preferred from 30 through 45 minutes, and overdue after 45 minutes;
- an interstitial is ineligible before 20 minutes, preferred from 20 through 30 minutes, and overdue after 30 minutes;
- a bumper is ineligible until four normal programs have played, is preferred after four, and is overdue after five.

In legacy mode, a normal program is any scheduled type other than bumper, promo, or interstitial. Cadence items do not increment the bumper's normal-program count. With Checkpoint 3A policy active, bumper cadence instead counts only substantial programming pieces and uses the configurable 3–5 default; promo and advertisement share one configurable mini-break cadence.

Minimum cadence eligibility is independent of the ordinary exact-asset cooldown and is not opened by the normal fallback ladder. When a cadence category is due, assets outside their exact cooldown are preferred. If every asset in an overdue cadence category is still inside its ordinary exact cooldown, that cooldown may be relaxed for the cadence insertion. This never permits an insertion before the cadence minimum. Multiple eligible cadence assets continue to use deterministic seeded selection.

The category mix and inventory-capacity planner remain centralized in `PlaylistPolicy`. Checkpoint 3A layers its versioned operator configuration over these existing targets rather than replacing them or creating a second scheduler.

## Checkpoint 3A programming-policy selection

The active policy changes sequencing while retaining the same eligible snapshot, target planner, deterministic seed, playlist schema, and planned history. The conceptual selection order is:

1. honor an overdue cadence insertion;
2. ask the current internal personality template for a lane;
3. use category deficit and inventory capacity to prefer an asset type;
4. enforce exact-asset protection and same-song substantial-item adjacency, then prefer a content group outside the recent substantial-piece lookback;
5. after three consecutive short substantial pieces by default, require an available full/personality alternative without weakening the existing repetition protections;
6. choose one `contentGroupId` family, applying the optional Active Campaign multiplier;
7. choose a presentation within that family using its sparse asset weight; and
8. relax preferences deterministically when real inventory cannot satisfy them.

Song-family choice and presentation choice are separate. A family with twelve eligible presentations therefore receives the same ordinary family weight as a family with one presentation. The active campaign defaults to 2.0x and changes the family weight once; it never multiplies by presentation count or bypasses Do Not Air or hard adjacency.

The exact asset retains a configurable preferred cooldown, initially two hours. The previous long time-based same-song model is replaced, only under active Checkpoint 3A policy, by:

- no adjacent substantial pieces with the same `contentGroupId` while any valid alternative exists; and
- a strong, configurable preference against a group seen in the previous two substantial pieces.

Substantial content uses existing types: music-video, lyric-video, visualizer, animated-visual, performance, short-form, vlog, and special. Bumper, promo, interstitial, and advertisement insertions do not reset adjacency or the lookback.

MUSIC-HEAVY, MIXED, and FAST-PACED are internal deterministic lane templates, not named shows. Missing lane inventory falls back safely. Full/short classification reuses the existing 60-second rule, short-form remains part of ordinary flow, and vlogs remain non-music personality content with consecutive-vlog protection.

`maximumConsecutiveShortPieces` defaults to three. Short-form always counts; other song-based music, performance, visualizer, and animated/cinematic presentations count when they are at most 60 seconds. A full presentation, vlog, or special resets the run. Bumper, promo, advertisement, and interstitial insertions are transparent to the count, so they cannot disguise an accidental Shorts-style block. When no legal non-short substantial candidate exists, the scheduler permits a deterministic, counted relaxation instead of deadlocking. Campaign weight is evaluated after this filter and cannot override it.

Bumpers rotate through least-recently-used eligible presentations and are preferred after three substantial pieces and due by five. Promo and advertisement assets share the configurable 30–45 minute mini-break interval; they are not scheduled as back-to-back commercial pods. Interstitial cadence remains separate and compatible.

## Hot rotation

Hot rotation uses metadata `rotationStartDate`, never filesystem timestamps:

| Age | Weight |
|---|---:|
| 0–7 days | 2.5× |
| 8–21 days | 2.0× |
| 22–45 days | 1.5× |
| 46+ days or null | 1.0× |

Weights feed the seeded deterministic choice. Hot preference cannot bypass same-song spacing or consecutive-vlog protection. When the selected hot preference cannot be used and a lower-weight candidate is required, the bypass is counted.

This release-age behavior remains the legacy default when `programming.json` is absent. Under Checkpoint 3A policy it defaults OFF, so it does not silently stack with an operator-selected campaign. `releaseAgeHotRotationEnabled` can explicitly opt back in; if enabled, the age weight is calculated once per content group rather than once per presentation.

## Legacy cooldowns and relaxation

The time-based rules in this section describe compatibility behavior when `programming.json` is absent. Active Checkpoint 3A policy instead uses the exact-asset cooldown plus substantial-item adjacency and lookback described above.

Different visual assets with the same `contentGroupId` are one song family. A music video, lyric video, visualizer, animated visual, performance, and short-form clip for one song all share one cooldown clock. Changing presentation type never bypasses history or song spacing. The preferred target is 90 minutes. A controlled relaxation may schedule the group from 60 through 90 minutes; ordinary fallback never schedules it below the 60-minute floor. Visualizers, animated visuals, and short-form have no special song-repeat escape hatch.

The 45-minute music-first rescue floor is not an ordinary candidate tier and cannot be used to improve category percentages. After two consecutive vlogs, if no candidate at or above the normal 60-minute floor is usable, the scheduler searches for song candidates whose shared content-group clock is at least 45 minutes old. A 45–60-minute song then replaces what would otherwise be vlog #3 or later. If no such song exists, the scheduler may record an emergency vlog-run violation. Song repeats below 45 minutes remain a later pathological forward-progress fallback.

If the limited catalog cannot satisfy every preference, the engine progressively opens rules in this order:

1. category airtime target;
2. exact asset cooldown;
3. hot-release preference;
4. no-consecutive-vlog preference, permitting a second vlog;
5. preferred 90-minute `contentGroupId` target, but only down to the 60-minute floor.

The scheduler prefers one vlog at a time, but a second consecutive vlog is a controlled pacing fallback and is preferable to relaxing song spacing. A third consecutive vlog is not part of ordinary scheduling. Before that emergency, the scheduler may use the music-first 45–60-minute rescue tier. `contentGroupCooldownRelaxations` counts only controlled 60–90-minute song spacing, `musicFirstRescueRelaxations` counts the rescue tier, and `emergencyContentGroupFloorViolations` identifies sub-45-minute repeats. If pathological inventory still cannot progress, third-or-later vlog runs and sub-45-minute song repeats use separately counted emergency stages; an absolute last-resort selection may report both violations.

Cadence misses and insertion counts are also reported. Cadence minimum spacing is not a relaxation stage: cadence assets are removed from consideration until their minimum window opens. With at least one policy-enabled asset, the explicit emergency stages guarantee forward progress rather than deadlocking.

## Playlist schema

Playlist schema version 1 is JSON with camel-case property names:

```json
{
  "schemaVersion": 1,
  "generatedAtUtc": "2026-09-27T12:00:00+00:00",
  "scheduleStartUtc": "2026-09-27T12:00:00+00:00",
  "seed": 20260927,
  "targetDurationSeconds": 21600,
  "actualDurationSeconds": 21742,
  "overrunSeconds": 142,
  "items": [
    {
      "sequence": 1,
      "assetId": "cash-rules-music-video",
      "contentGroupId": "cash-rules",
      "title": "Cash Rules",
      "type": "music-video",
      "subtype": null,
      "relativePath": "Music Videos/Nzyte - CASH RULES (Official Music Video).mp4",
      "durationSeconds": 213.4,
      "startOffsetSeconds": 0
    }
  ],
  "excludedAssets": [],
  "summary": {
    "eligibleAssets": 39,
    "excludedAssets": 0,
    "categoryTargetRelaxations": 9,
    "exactAssetCooldownRelaxations": 6,
    "newReleasePreferenceBypasses": 2,
    "contentGroupCooldownRelaxations": 0,
    "musicFirstRescueRelaxations": 1,
    "emergencyContentGroupFloorViolations": 0,
    "consecutiveVlogViolations": 0,
    "emergencyVlogRunViolations": 0,
    "bumperCadenceMisses": 0,
    "promoCadenceMisses": 0,
    "interstitialCadenceMisses": 0,
    "bumperInsertions": 0,
    "promoInsertions": 8,
    "interstitialInsertions": 0,
    "programmingPolicyActive": true,
    "programmingPolicyRevision": 1,
    "programmingPatternFallbacks": 0,
    "contentGroupClusterRelaxations": 0,
    "contentGroupAdjacencyViolations": 0,
    "shortRunRelaxations": 0,
    "maximumObservedConsecutiveShortPieces": 3,
    "configuredAirtimeTargetPercentages": {
      "animated-visual": 20.0,
      "lyric-video": 15.0,
      "music-video": 20.0,
      "performance": 10.0,
      "short-form": 5.0,
      "visualizer": 15.0,
      "vlog": 15.0
    },
    "effectiveAirtimeTargetPercentages": {
      "animated-visual": 28.9,
      "lyric-video": 21.68,
      "music-video": 8.58,
      "performance": 4.27,
      "short-form": 0.0,
      "visualizer": 21.57,
      "vlog": 15.0
    },
    "capacityLimitedCategories": ["music-video", "performance", "short-form"],
    "redistributedTargetAirtimeSeconds": 4784.4,
    "airtimePercentages": {
      "animated-visual": 20.0,
      "lyric-video": 15.0,
      "music-video": 20.0,
      "performance": 10.0,
      "short-form": 5.0,
      "visualizer": 15.0,
      "vlog": 15.0
    }
  }
}
```

Paths are library-relative and use `/` separators on every platform. Durations and offsets are seconds and retain millisecond precision. Target-planning diagnostics, the music-first rescue, cadence insertion, emergency-violation counters, `programmingPolicyActive`, `programmingPolicyRevision`, pattern fallbacks, song-cluster relaxations, unavoidable adjacency violations, short-run relaxations, and the maximum observed short run are additive summary fields in playlist schema version 1; the playlist and history schema versions are unchanged.

## History schema and playlist boundaries

History schema version 1 stores the prior schedule end and recent play starts:

```json
{
  "schemaVersion": 1,
  "scheduleEndUtc": "2026-09-27T18:02:22+00:00",
  "plays": [
    {
      "assetId": "cash-rules-music-video",
      "contentGroupId": "cash-rules",
      "type": "music-video",
      "playedAtUtc": "2026-09-27T17:58:48+00:00"
    }
  ]
}
```

The next playlist starts no earlier than the prior `scheduleEndUtc`. This prevents an asset or song at the end of one JSON file from immediately repeating at the beginning of the next. After generation, history is trimmed to the longest cooldown plus a 15-minute safety margin, so it cannot grow forever.

Missing history starts cleanly. Malformed, unsupported, or internally inconsistent history fails with a clear error; it is never silently ignored or overwritten.

History remains planned programming history under Checkpoint 3A. It is not an air log and is never merged with schema-v2 station resume state. Programming configuration changes affect only later normal playlist-generation writes.

For manual/static operation, generate each next playlist block with the same current history path. Do not delete or reset history casually: doing so discards the cross-playlist exact-asset and same-song cooldown context.

Checkpoint 3B1 offers a safer rolling-planning transaction for future use. It imports an explicit planned-history genesis once, then stores immutable content-addressed before/after history snapshots and commits each generated six-hour block with the same atomic manifest replacement. It calls the same accepted `PlaylistGenerator`, emits the same playlist schema version 1, and validates each result through the existing `BroadcastPlanner`. Ordinary `history.json` is not the rolling transaction authority. See [rolling-programming.md](rolling-programming.md).

## Scope boundary

Playlist generation produces finite schedules and bounded planned history only. Checkpoint 3B1 can prepare immutable future blocks, but the separate `broadcast` and station commands do not consume or append them automatically. Rolling handoff, an air log, Skip/Force Play controls, a Web UI, dayparts, named shows, YouTube monitoring, and filesystem-driven changes to a running queue remain unimplemented.
