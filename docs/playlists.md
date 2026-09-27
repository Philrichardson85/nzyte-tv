# Playlist and programming engine

NZYTE TV v0.2B generates deterministic, inspectable schedules from normalized library assets. It does not broadcast, re-encode, edit metadata, or modify media.

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

Generation scans the normalized library once. An asset is schedulable only when the existing v0.2A eligibility rules pass:

- the normalized media exists;
- the technical `.nzytetv.json` manifest exists;
- valid `.nzytetv.meta.json` programming metadata exists;
- the asset is enabled;
- song-based metadata has a valid catalog `contentGroupId`;
- FFprobe returns a positive duration;
- the active policy gives the asset type an airtime target or cadence.

Missing, disabled, invalid, unresolved, duration-less, duplicate-identity, or policy-disabled assets are excluded with visible reasons. Schedule generation never repairs or mutates them. Zero participating eligible assets is a clear failure.

## Default policy

The strongly typed policy is centralized in `NzyteTv.Core`. The v0.2B current-library defaults are:

| Rule | Default |
|---|---:|
| Target duration | 6 hours |
| Music-video airtime | 50% |
| Lyric-video airtime | 25% |
| Vlog airtime | 25% |
| Exact asset cooldown | 2 hours |
| Same `contentGroupId` preferred cooldown | 90 minutes |
| Same `contentGroupId` normal floor | 60 minutes |
| Music-first rescue floor | 45 minutes, only before vlog #3+ |
| Consecutive vlogs | one preferred; two permitted as fallback |
| Bumper cadence | every 4–5 normal programs |
| Interstitial cadence | every 20–30 minutes |
| Promo cadence | every 30–45 minutes |

Airtime targets use scheduled seconds, not item counts. Only targets represented by eligible inventory are normalized into the active mix. Performance, short-form, advertisement, and special assets remain supported but require a future policy target/cadence before they participate. Bumpers, promos, and interstitials participate only when eligible inventory exists; their absence never prevents generation.

Cadence windows have a minimum eligibility boundary as well as a preferred/overdue boundary:

- a promo is ineligible before 30 minutes, preferred from 30 through 45 minutes, and overdue after 45 minutes;
- an interstitial is ineligible before 20 minutes, preferred from 20 through 30 minutes, and overdue after 30 minutes;
- a bumper is ineligible until four normal programs have played, is preferred after four, and is overdue after five.

A normal program is any scheduled type other than bumper, promo, or interstitial. Cadence items do not increment the bumper's normal-program count.

Minimum cadence eligibility is independent of the ordinary exact-asset cooldown and is not opened by the normal fallback ladder. When a cadence category is due, assets outside their exact cooldown are preferred. If every asset in an overdue cadence category is still inside its ordinary exact cooldown, that cooldown may be relaxed for the cadence insertion. This never permits an insertion before the cadence minimum. Multiple eligible cadence assets continue to use deterministic seeded selection.

The future 40/20/20/20 music-video, lyric-video, performance, and vlog mix can be represented by another `PlaylistPolicy` without changing scheduler logic. External policy JSON is intentionally deferred.

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

Different visual assets with the same `contentGroupId` are one song family. A music video, lyric video, performance, and short-form clip for one song all share one cooldown clock. The preferred target is 90 minutes. A controlled relaxation may schedule the group from 60 through 90 minutes; ordinary fallback never schedules it below the 60-minute floor. Short-form has no special song-repeat escape hatch.

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
    "airtimePercentages": {
      "music-video": 51.2,
      "lyric-video": 24.0,
      "vlog": 24.8
    }
  }
}
```

Paths are library-relative and use `/` separators on every platform. Durations and offsets are seconds and retain millisecond precision. The music-first rescue, cadence insertion, and emergency-violation counters are additive summary fields in playlist schema version 1; the playlist and history schema versions are unchanged.

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

## Scope boundary

v0.2B produces schedules and bounded history only. It does not implement YouTube broadcasting, RTMP, a broadcaster service, a continuous playback loop, systemd integration, live reload, or filesystem watching.
