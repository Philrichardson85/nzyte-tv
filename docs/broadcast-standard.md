# NZYTE TV broadcast standard

Every file admitted to the future NZYTE TV broadcast library must first be normalized and then pass independent verification. A successful FFmpeg exit code alone is not sufficient.

## Required format

| Area | Requirement | Reason |
|---|---|---|
| Container | MP4 with `+faststart` | Widely supported; moves MP4 metadata to the beginning for prompt access. |
| Video codec | H.264/AVC, `libx264`, High profile | Hardware- and platform-friendly delivery format with a predictable encoder. |
| Pixel format | `yuv420p` | Broad decoder and platform compatibility. |
| Canvas | 1920x1080 | One stable output geometry for broadcast. |
| Aspect ratio | Scale to fit, then pad | Preserves the source image without stretching or cropping. |
| Frame rate | Constant 30 fps | Gives scheduling and keyframe cadence a deterministic time base. |
| GOP | 60 frames | At 30 fps, 60 frames is exactly two seconds. |
| Keyframes | `-g 60 -keyint_min 60 -sc_threshold 0` | Enforces the two-second cadence and prevents scene cuts from adding extra keyframes. |
| Video rate | 6000 kbps target/max, 12000 kbps buffer | Provides a bounded, predictable delivery bitrate. |
| Audio | AAC, 48 kHz, stereo, 192 kbps | Provides a uniform stream layout and standard broadcast sample rate. |

Video-only sources receive a silent stereo AAC track. This keeps downstream broadcasting simple because every accepted file has one video stream and one audio stream.

## Why keyframes are verified from timestamps

The real test that established this rule used an original H.264/AAC 1080p file on the Raspberry Pi. FFmpeg streamed that file successfully using stream copy, but YouTube reported poor stream health because its keyframes were too far apart.

The file was normalized with a two-second keyframe interval. The normalized file was then streamed with FFmpeg using `-c copy`, and YouTube reported **Excellent** stream health.

Container or codec metadata does not prove where keyframes actually occur. NZYTE TV therefore asks FFprobe for real keyframe frames, reads their timestamps, and calculates consecutive intervals. Each interval must be approximately 2.000 seconds with a 0.100-second tolerance. A long file with no consecutive interval cannot pass. The first keyframe must also occur at the beginning within the same tolerance.

This admission check is the foundation for later stream-copy broadcasting: future scheduling and broadcast components can rely on every library file already having the required cadence and stream layout.

## Verification checks

Independent verification requires:

- a usable MP4 container;
- an H.264 High-profile video stream;
- 1920x1080 `yuv420p` video;
- a frame rate within 0.01 fps of 30;
- actual keyframe timestamps at approximately two-second intervals;
- an AAC audio stream at 48,000 Hz with two channels.

Any failed check makes the file **NOT BROADCAST READY** and produces a non-zero process exit code.
