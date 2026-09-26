# NZYTE TV Engineering Instructions

NZYTE TV is a cross-platform broadcast automation project.

## Platform targets

Primary targets:

- Windows x64
- Linux ARM64
- Raspberry Pi 4 Model B / 4 GB
- Ubuntu 22.04 Linux

Target framework: .NET 10.

## Media

FFmpeg and FFprobe are external runtime dependencies.

Never implement codec functionality in managed code.

Do not execute FFmpeg through a shell unless there is an unavoidable documented reason.

Use `ProcessStartInfo.ArgumentList` for arguments.

Assume file paths can contain spaces, apostrophes, parentheses, Unicode, and other valid filesystem characters.

## Safety

Never overwrite original media unless the user explicitly requests it. Even explicit output overwrite behavior must not permit a source file to be used as its own destination.

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
