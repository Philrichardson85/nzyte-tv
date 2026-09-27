# NZYTE TV Engineering Instructions

NZYTE TV is a cross-platform broadcast automation project.

## Platform targets

Primary targets:

- Windows x64
- Linux ARM64
- Raspberry Pi 4 Model B / 4 GB
- Ubuntu 24.04 ARM64 Linux

Target framework: .NET 10.

Linux ARM64 must remain supported. The verified deployment environment is Ubuntu Desktop 24.04.5 LTS on a Raspberry Pi 4 with FFmpeg/FFprobe `6.1.1-3ubuntu5`.

Do not introduce Windows-only paths, path separators, process assumptions, or APIs into shared application code.

## Media

FFmpeg and FFprobe are external runtime dependencies.

Never implement codec functionality in managed code.

Do not execute FFmpeg through a shell unless there is an unavoidable documented reason.

Use `ProcessStartInfo.ArgumentList` for arguments.

Assume file paths can contain spaces, apostrophes, parentheses, Unicode, and other valid filesystem characters.

Preserve paths with spaces and capitalization. Do not split or reconstruct media paths through shell strings.

Do not assume OMX exists. The verified Ubuntu FFmpeg build includes `--disable-omx`.

Do not assume that an encoder listed by FFmpeg is usable on Raspberry Pi hardware or produces compliant broadcast output. `h264_v4l2m2m` availability is confirmed, but its tested output is not yet validated for NZYTE TV because the synthetic test produced timestamp and initial-picture/keyframe problems. Keep software `libx264` as the verified normalization path unless a behavioral change is explicitly designed and tested.

Media-library scanners must filter an explicit, case-insensitive allowlist of supported media-file extensions. They must not treat every file in a category directory as playable media, and must ignore filesystem metadata such as `System Volume Information`.

Batch normalization must preserve source-relative category paths, reject overlapping source and destination roots, and prevent multiple sources from overwriting the same normalized destination. A per-file failure must be recorded without aborting later files; cancellation is the exception and must stop promptly.

Existing batch destinations may be treated as resumable skips only after independent verification passes. Invalid existing destinations must never be silently counted as broadcast ready.

The independent verification check is necessary but not sufficient for a batch skip. The destination's source manifest must also match the current source-relative path, size, last-modified UTC, manifest schema, and broadcast-profile version. Missing, corrupt, or stale manifests must fail safely by forcing normalization.

Increment `BroadcastStandard.ProfileVersion` whenever a broadcast-standard change could make an existing normalized output stale. Profile-version changes must invalidate source manifests.

Write source manifests atomically only after normalization and verification succeed. Never create or update a matching manifest for an unverified output.

## Production storage layout

The Raspberry Pi production media drive is mounted at `/srv/nzyte-tv/media`. Its `source/` directory contains original/master media, and its `library/` directory contains only normalized and verified broadcast assets.

Application and operational examples should use the stable convenience paths `/srv/nzyte-tv/source` and `/srv/nzyte-tv/library`, which are symlinks to the corresponding directories on the external drive. Preserve correct behavior when roots are symlinks, and remember that maintenance tools such as `find` and `du` may require `-L` to follow them.

Future broadcaster and playlist components must consume `/srv/nzyte-tv/library`, never `/srv/nzyte-tv/source`. Keep large source and normalized media on the external drive; the SD card is primarily for the OS, application, repository, logs, playlists, and working data.

## Safety

Source media must never be overwritten during normalization unless the product behavior is explicitly changed, reviewed, documented, and tested. Current `--overwrite` behavior replaces only an existing destination and must never permit a source file to be used as its own destination.

Normalization output must pass automatic verification before it is published or treated as broadcast ready. A successful FFmpeg exit code alone is insufficient.

Never commit credentials, API keys, YouTube stream keys, passwords, or tokens. Future YouTube stream keys must never be stored in source control.

Future secrets must be supplied through environment variables or an appropriate secret store.

## Architecture

Keep business/domain rules independent of FFmpeg implementation details.

`NzyteTv.Core` must not depend on `NzyteTv.Media`.

Media-process implementation belongs in `NzyteTv.Media`.

CLI presentation belongs in `NzyteTv.Cli`.

Prefer testable services and interfaces over static process-launch code.

## Quality

Nullable reference types remain enabled.

All new behavioral logic should have tests.

Run:

```text
dotnet build NzyteTv.slnx
dotnet test NzyteTv.slnx
```

before considering a change complete.

Keep README setup instructions synchronized with actual behavior.

Do not claim commands or platforms were tested unless they were actually tested.
