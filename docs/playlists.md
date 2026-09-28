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

Missing, disabled, invalid, unresolved, duration-less, duplicate-identity, or policy-disabled assets are excluded with visible reasons. Schedule generation never repairs or mutates them. Zero participating eligible assets is a clear failure.

`visualizer` represents a full-song static or lightly animated graphical presentation. `animated-visual` represents an animated, narrative, cinematic, anime/movie-style, AI-animated, or similar extended song presentation. Both are normal music programming, require a valid catalog relationship, and are never treated as promo, bumper, interstitial, advertisement, or vlog cadence content.

## Default policy

The strongly typed policy is centralized in `NzyteTv.Core`. The provisional normal-program defaults are:

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
| Bumper cadence | every 4–5 normal programs |
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

A normal program is any scheduled type other than bumper, promo, or interstitial. Cadence items do not increment the bumper's normal-program count.

Minimum cadence eligibility is independent of the ordinary exact-asset cooldown and is not opened by the normal fallback ladder. When a cadence category is due, assets outside their exact cooldown are preferred. If every asset in an overdue cadence category is still inside its ordinary exact cooldown, that cooldown may be relaxed for the cadence insertion. This never permits an insertion before the cadence minimum. Multiple eligible cadence assets continue to use deterministic seeded selection.

The mix is provisional and remains centralized in `PlaylistPolicy` so it can be tuned after the expanded production library is ingested and measured. External policy JSON is intentionally deferred.

## Hot rotation

Hot rotation uses metadata `rotationStartDate`, never filesystem timestamps:

| Age | Weight |
|---|---:|
| 0–7 days | 2.5× |
| 8–21 days | 2.0× |
| 22–45 days | 1.5× |
| 46+ days or null | 1.0× |

Weights feed the seeded deterministic choice. Hot preference cannot bypass same-song spacing or consecutive-vlog protection. When the selected hot preference cannot be used and a lower-weight candidate is required, the bypass is counted.

## Cooldowns and relaxation

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

Paths are library-relative and use `/` separators on every platform. Durations and offsets are seconds and retain millisecond precision. Target-planning diagnostics, the music-first rescue, cadence insertion, and emergency-violation counters are additive summary fields in playlist schema version 1; the playlist and history schema versions are unchanged.

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

During ongoing station operation, generate each next playlist block with the same current history path. Do not delete or reset history casually: doing so discards the cross-playlist exact-asset and same-song cooldown context.

## Scope boundary

Playlist generation produces schedules and bounded history only. The separate `broadcast` command can play supplied playlist files, but it does not automatically generate future blocks, run as a systemd service, monitor YouTube, or watch the filesystem.
