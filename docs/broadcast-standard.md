# NZYTE TV broadcast standard

Every file admitted to the production NZYTE TV library at `/srv/nzyte-tv/library` must first be normalized and pass verification. A successful FFmpeg process alone is not sufficient.

## Required format

| Area | Requirement | Normalization setting or verification rule |
|---|---|---|
| Container | Usable MP4 | MP4 output with `+faststart`; FFprobe must identify an MP4-compatible container |
| Video codec | H.264/AVC | Software encoder `libx264`; verifier requires H.264 |
| Video profile | High | `-profile:v high` |
| Pixel format | `yuv420p` | `-pix_fmt yuv420p` |
| Resolution | 1920x1080 | Scale to fit and pad; never stretch the source |
| Frame rate | Constant 30 fps | `-r 30 -fps_mode cfr`; verifier tolerance is 0.01 fps |
| GOP | 60 frames | 60 frames at 30 fps is two seconds |
| Keyframes | Approximately every 2.000 seconds | `-g 60 -keyint_min 60 -sc_threshold 0`; actual timestamp tolerance is 0.100 seconds |
| Video bitrate | 6000 kbps target and maximum | `-b:v 6000k -maxrate 6000k -bufsize 12000k` |
| Audio codec | AAC | `-c:a aac` |
| Audio sample rate | 48 kHz | `-ar 48000` |
| Audio layout | Stereo | `-ac 2` |
| Audio bitrate | 192 kbps target | `-b:a 192k` |

Video-only sources receive a silent 48 kHz stereo AAC track so every normalized file has a consistent video-plus-audio stream layout.

## Aspect-ratio handling

Normalization scales the source down or up to fit inside 1920x1080 while preserving its aspect ratio, then pads the unused area. The source is not stretched or cropped.

The production source library includes `Vlog 2 episode 3.mp4` at 2628x1440. It completed normalization and verification as part of the full 39-file Raspberry Pi production run. Its unusual source aspect ratio remains useful for a future dedicated scale-and-pad regression test.

## Why keyframes are verified from timestamps

The operational test that established the keyframe requirement used an original H.264/AAC 1080p file on the Raspberry Pi. FFmpeg successfully streamed it using stream copy, but YouTube reported poor stream health because keyframes were too far apart.

After the file was normalized to a two-second keyframe interval, it was streamed again with FFmpeg using `-c copy`, and YouTube reported **Excellent** stream health.

Codec and GOP metadata do not prove where keyframes actually occur. NZYTE TV therefore asks FFprobe for actual keyframe frames, reads their timestamps, and calculates intervals between consecutive keyframes. Each interval must be within 0.100 seconds of 2.000 seconds. The first keyframe must be at the beginning within the same tolerance. A long file without enough keyframes to establish the cadence fails.

## Verified Raspberry Pi reference case

The following real file was inspected, rejected by verification, normalized, automatically verified, and then independently verified on the Raspberry Pi 4.

### Source

```text
Lady Lady - Nzyte (Official Music Video).mp4
```

Before normalization:

| Property | Source value | Initial verification |
|---|---:|---|
| Container | Usable MP4 | PASS |
| Video stream | Present | PASS |
| Video codec/profile | H.264 High | PASS |
| Resolution | 2560x1440 | FAIL |
| Frame rate | 24 fps | FAIL |
| Pixel format | `yuv420p` | PASS |
| Keyframe spacing | 6.25 seconds | FAIL |
| Video bitrate | Approximately 18.6 Mbps | Informational |
| Audio stream | Present | PASS |
| Audio codec | AAC | PASS |
| Audio sample rate | 44.1 kHz | FAIL |
| Audio channels | Stereo | PASS |
| Duration | 2:27.098 | Informational |
| File size | Approximately 330.35 MiB | Informational |

The initial result was `RESULT: NOT BROADCAST READY`.

### Normalization

The command was run with `/srv/nzyte-tv/work` as the current directory. It produced:

```text
/srv/nzyte-tv/work/BroadcastReady/Lady Lady - Nzyte (Official Music Video).mp4
```

Normalization plus automatic verification took 13 minutes 38 seconds on the Raspberry Pi 4. This is a measured reference, not a guaranteed duration for other media.

That path was the temporary acceptance-test output used at the time. It was later removed from `/srv/nzyte-tv/work`; permanent production batch outputs now belong under `/srv/nzyte-tv/library`. This clarification does not change the broadcast standard or the recorded test result.

### Normalized result

| Property | Normalized value | Verification |
|---|---:|---|
| Container | Usable MP4 | PASS |
| Video codec/profile | H.264 High | PASS |
| Resolution | 1920x1080 | PASS |
| Frame rate | 30 fps | PASS |
| Pixel format | `yuv420p` | PASS |
| Keyframe spacing | 2 seconds | PASS |
| Video bitrate | Approximately 5926.9 kbps | Informational |
| Audio codec | AAC | PASS |
| Audio sample rate | 48 kHz | PASS |
| Audio channels | Stereo | PASS |
| Audio bitrate | Approximately 200.2 kbps | Informational |
| Duration | 2:27.100 | Informational |
| File size | Approximately 107.6 MiB | Informational |

An independent `verify` command after normalization reported:

```text
RESULT: BROADCAST READY
```

All required checks passed.

## Full-library production validation

The v0.1 media-normalization and verification milestone completed production acceptance on the Raspberry Pi 4 / `linux-arm64` deployment. The first full-library run normalized and verified all 39 source videos with no failures in 06:21:07. An unchanged integrity run then independently re-verified and skipped all 39 matching outputs with no failures in 00:04:42.

Final validation found 39 broadcast-ready videos and 39 matching source manifests in the 3.3 GB normalized library at `/srv/nzyte-tv/library`. Together with the successful YouTube Live stream-copy acceptance below, these results validate the broadcast standard across the complete production media library.

## Hardware encoder status

The deployed Ubuntu FFmpeg build lists and can start the Raspberry Pi's `h264_v4l2m2m` hardware encoder. A synthetic 1280x720 30 fps test ran at approximately 5.06x real time using `/dev/video11` and the `bcm2835-codec-encode` device.

That test also emitted `Non-monotonic DTS`, and later FFprobe output reported a missing keyframe or missing picture at the beginning. Consequently, hardware availability is confirmed, but that output path is **not validated** for NZYTE TV's broadcast standard.

The current verified normalization workflow uses software `libx264`. Do not substitute a listed hardware encoder without representative normalization, timestamp, and independent verification tests.

## Verification result

Independent verification requires every one of these checks to pass:

- usable MP4;
- video stream present;
- H.264 High;
- 1920x1080;
- 30 fps within tolerance;
- `yuv420p`;
- actual two-second keyframe cadence;
- audio stream present;
- AAC;
- 48 kHz;
- stereo.

Any failed check produces `RESULT: NOT BROADCAST READY` and a non-zero process exit code. Only an all-pass report produces `RESULT: BROADCAST READY`.

## YouTube Live acceptance

The broadcast standard was validated end to end on the Raspberry Pi 4 with three normalized production files:

- `Nzyte - CASH RULES (Official Music Video).mp4`
- `Nzyte Vlog Episode 3.mp4`
- `Nzyte - American Dreams (Official Video).mp4`

Each normalized MP4 was sent directly to YouTube Live with FFmpeg stream-copy (`-c copy`), FLV, and RTMPS ingest. All three displayed correctly, had correct audio and aspect ratio, maintained A/V synchronization, ran at approximately `speed=1.00x`, and received YouTube **Excellent** stream health.

This confirms the acceptance path:

```text
Production source
-> normalize-library
-> broadcast-ready output
-> verification PASS
-> Raspberry Pi
-> FFmpeg stream-copy
-> YouTube Live
-> Excellent stream health
```

It does not make live broadcasting part of the v0.1 application.

### Expected live FLV shutdown warning

Manually stopping a live FLV/RTMP stream can produce:

```text
Failed to update header with correct duration.
Failed to update header with correct filesize.
```

These warnings are harmless for this manually stopped live output. Future broadcaster commands may use `-flvflags no_duration_filesize` to suppress them. This flag is not part of normalization and no broadcaster command is currently implemented.
