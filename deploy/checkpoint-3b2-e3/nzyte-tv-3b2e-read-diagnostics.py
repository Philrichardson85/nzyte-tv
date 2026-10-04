#!/usr/bin/env python3

import datetime
import json
import re
import sys


ATTEMPT_ID_PATTERN = re.compile(r"^[0-9a-fA-F]{32}$")
DURATION_PATTERN = re.compile(
    r"^(?:(?P<days>[0-9]+)\.)?"
    r"(?P<hours>[0-9]{2}):(?P<minutes>[0-9]{2}):(?P<seconds>[0-9]{2})"
    r"(?:\.(?P<fraction>[0-9]{1,7}))?$"
)


def fail() -> None:
    raise ValueError("invalid advisory diagnostics")


def duration_microseconds(value: object) -> int:
    if not isinstance(value, str):
        fail()
    match = DURATION_PATTERN.fullmatch(value)
    if match is None:
        fail()
    hours = int(match.group("hours"))
    minutes = int(match.group("minutes"))
    seconds = int(match.group("seconds"))
    if hours > 23 or minutes > 59 or seconds > 59:
        fail()
    fraction = (match.group("fraction") or "").ljust(7, "0")
    ticks = int(fraction or "0")
    total_seconds = (
        int(match.group("days") or "0") * 86400
        + hours * 3600
        + minutes * 60
        + seconds
    )
    return total_seconds * 1_000_000 + ticks // 10


def utc_epoch(value: object) -> int:
    if not isinstance(value, str):
        fail()
    normalized = value[:-1] + "+00:00" if value.endswith("Z") else value
    parsed = datetime.datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        fail()
    return int(parsed.timestamp())


def read_snapshot(path: str) -> tuple[str, int, int, int]:
    with open(path, "r", encoding="utf-8") as stream:
        document = json.load(stream)
    if not isinstance(document, dict) or document.get("schemaVersion") != 1:
        fail()
    attempts = document.get("attempts")
    if not isinstance(attempts, list) or not attempts:
        fail()
    attempt = attempts[-1]
    if not isinstance(attempt, dict):
        fail()

    attempt_id = attempt.get("attemptId")
    ffmpeg_pid = attempt.get("ffmpegPid")
    if not isinstance(attempt_id, str) or ATTEMPT_ID_PATTERN.fullmatch(attempt_id) is None:
        fail()
    if not isinstance(ffmpeg_pid, int) or isinstance(ffmpeg_pid, bool) or ffmpeg_pid <= 0:
        fail()
    if not isinstance(attempt.get("processStartTimeUtc"), str):
        fail()
    if attempt.get("processExitTimeUtc") is not None or attempt.get("processExitCode") is not None:
        fail()

    return (
        attempt_id.lower(),
        ffmpeg_pid,
        duration_microseconds(attempt.get("latestOutputTime")),
        utc_epoch(attempt.get("lastAdvancingProgressTimeUtc")),
    )


def main() -> int:
    if len(sys.argv) != 2:
        return 2
    try:
        snapshot = read_snapshot(sys.argv[1])
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError, OverflowError):
        return 1
    print("\t".join(str(value) for value in snapshot))
    return 0


if __name__ == "__main__":
    sys.exit(main())
