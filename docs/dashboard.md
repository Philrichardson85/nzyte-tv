# Operations dashboard (Checkpoint 3B3-A / 3B3-B1 / 3B3-B2-C)

Checkpoint 3B3-A adds a separate ASP.NET Core status dashboard. Checkpoint 3B3-B1 retains that isolated status path and adds only the Spotlight Record programming control through a second, narrowly privileged local operations helper. Neither process runs inside, starts, stops, signals, or restarts the broadcaster. Neither invokes FFmpeg, the CLI, a shell, or systemd. Restarting or stopping either process does not stop the broadcaster.

The dashboard presents a sanitized projection of three accepted runtime documents:

- `/var/lib/nzyte-tv/state.json`;
- `/var/lib/nzyte-tv/rolling-state.json`; and
- `/var/lib/nzyte-tv/rolling-state.json.replenishment.json`.

It does not read the broadcast destination, secrets environment, FFmpeg diagnostics, journal, rolling manifest, or arbitrary filesystem paths. The replenishment document remains advisory and is shown only when its lineage and anchor agree with the rolling execution state.

## What the status means

The dashboard shows local station, broadcast, FFmpeg-process, current-item, rolling-block, and buffer evidence. Missing, malformed, stale, or contradictory evidence is reported as unavailable or degraded, never silently converted to `STOPPED` or zero.

The Now Playing section uses the accepted current title, item type, and queue position. Exact elapsed time, item duration, and Next Up are deliberately absent because the persisted state does not establish them reliably.

The version in the header is the **dashboard assembly version**. It is not the running station binary version.

> Broadcast status reflects the local NZYTE TV broadcaster. Public YouTube playback is not independently verified.

## HTTP surface

- `GET /` renders the Razor Pages dashboard.
- `GET /api/v1/status` returns the versioned, allowlisted JSON projection with `Cache-Control: no-store`.
- `GET /healthz` means only that the dashboard HTTP process can answer. It reveals no station health.
- `GET /api/v1/programming/spotlight` reads the current Spotlight state and validated catalog choices through the operations helper.
- `POST /api/v1/programming/spotlight` enables or updates Spotlight with an expected configuration revision.
- `POST /api/v1/programming/spotlight/disable` disables Spotlight with an expected configuration revision.
- `GET /api/v1/media-library` reads the sanitized media-refresh feature/generation summary through the operations helper.
- `POST /api/v1/media-library/refresh` requests an asynchronous refresh using the metadata revision currently shown to the operator.
- `GET /api/v1/media-library/operations/{operationId}` polls a sanitized durable refresh result.

The three allowlisted POST endpoints require ASP.NET Core antiforgery validation, strict JSON, and small request bodies. There are no PUT, PATCH, DELETE, bootstrap/migration, start, stop, restart, reconnect, planner-generation, playlist, upload, shell, or systemd controls.

The browser polls the status endpoint every five seconds. The server maintains one short-lived in-memory snapshot, so browser polling does not independently read every state file. A refresh failure produces a safe unavailable snapshot and does not terminate the web process.

## Binding and remote operator access

The application configures exactly one Kestrel listener on IPv4 loopback. The default endpoint is:

```text
http://127.0.0.1:5080
```

Only `Dashboard:Port` is supported for listener configuration. Alternative ASP.NET Core URL and listener settings—including `urls`, HTTP/HTTPS port settings, and `Kestrel:Endpoints`—cause startup to fail closed. This applies in Production, Testing, and every other environment; `ASPNETCORE_URLS`, `DOTNET_URLS`, command-line `--urls`, and Kestrel endpoint environment settings cannot enable wildcard or remote binding. Direct LAN and public binding are not supported in this checkpoint and require later authentication, HTTPS, and firewall design.

Use an SSH tunnel from the operator workstation:

```bash
ssh -N -L 15080:127.0.0.1:5080 u24@u24-desktop
```

Keep that SSH terminal open and browse to `http://127.0.0.1:15080` on the Windows workstation. The HTTP connection remains inside the SSH tunnel.

## Configuration

ASP.NET Core configuration can override these non-secret settings with environment variables:

| Environment variable | Default on Linux |
|---|---|
| `Dashboard__Port` | `5080` |
| `Dashboard__BrowserRefreshSeconds` | `5` |
| `Dashboard__StatusRefreshSeconds` | `5` |
| `Dashboard__StationStatePath` | `/var/lib/nzyte-tv/state.json` |
| `Dashboard__RollingStatePath` | `/var/lib/nzyte-tv/rolling-state.json` |
| `Dashboard__ReplenishmentStatePath` | `/var/lib/nzyte-tv/rolling-state.json.replenishment.json` |
| `Dashboard__OperationsSocketPath` | `/run/nzyte-tv-operations/operations.sock` |

All state paths must be absolute and distinct. They are process configuration only; the browser cannot supply paths. Windows development defaults to a `dashboard-state` directory beside the executable unless tests or local configuration provide temporary absolute paths.

The Operations helper also recognizes the following B2-C settings, all of which are deployment inputs rather than browser inputs:

| Environment variable | B2-C default |
|---|---|
| `Operations__MediaLibrary__Enabled` | `false` |
| `Operations__MediaLibrary__MediaRoot` | unset |
| `Operations__MediaLibrary__MetadataRoot` | unset |
| `Operations__MediaLibrary__InboxRoot` | unset (the B2-B media-root convention is used only after the feature is enabled with otherwise valid trusted roots) |

No production values are supplied by B2-C. With the section absent, the existing B1 helper and Spotlight routes start normally and media refresh reports disabled. An enabled but invalid configuration fails only the media feature closed.

## Publish

Publish the dashboard separately from the broadcaster into a workstation staging directory:

```bash
dotnet publish \
  src/NzyteTv.Dashboard/NzyteTv.Dashboard.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o artifacts/publish/dashboard-linux-arm64
```

This produces staging output only. Checkpoint implementation and workstation verification do not copy output to the Pi or enable a service. A later dashboard deployment or upgrade must use an operator-reviewed staged installation procedure; do not publish directly over a live dashboard directory.

Publish the operations helper to a separate staging directory with the same runtime settings:

```bash
dotnet publish \
  src/NzyteTv.Operations/NzyteTv.Operations.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o artifacts/publish/operations-linux-arm64
```

## Independent systemd templates

The released 3B3-A unit remains unchanged. The 3B3-B1 candidates are [nzyte-tv-dashboard.service](../deploy/checkpoint-3b3-b1/nzyte-tv-dashboard.service), [nzyte-tv-operations.service](../deploy/checkpoint-3b3-b1/nzyte-tv-operations.service), a neutral [programming-catalog mount template](../deploy/checkpoint-3b3-b1/nzyte-tv-programming-catalog.mount.template), and a [production-service dependency drop-in](../deploy/checkpoint-3b3-b1/nzyte-tv.service.d/programming-catalog.conf). They assume dedicated `nzyte-dashboard` and `nzyte-ops` users that do not yet exist and must be created during a separately reviewed Pi acceptance procedure. The complete staged migration and rollback procedure is in [Checkpoint 3B3-B1 Raspberry Pi deployment plan](deployment-3b3-b1.md).

Production media is exFAT, so it cannot supply a narrow per-directory POSIX write boundary for the helper. The accepted candidate design moves the whole programming catalog onto ext4 at `/var/lib/nzyte-tv-programming/catalog` and bind-mounts it at the unchanged application path `/srv/nzyte-tv/media/catalog`. The application media root remains `/srv/nzyte-tv/media`; no special catalog path is added. The mount depends on the parent media mount, and both the broadcaster and helper require the catalog mount so they fail closed instead of falling back to stale files underneath it.

`nzyte-programming` contains only `u24` and `nzyte-ops`. The ext4 catalog directory is intended to be `u24:nzyte-programming` mode `2770`, with migrated JSON files mode `0660`. The helper's `UMask=0007` is compatible with group-readable/writable atomic replacements. `nzyte-dashboard` is deliberately excluded from that group and receives no `/srv/nzyte-tv` access.

Separately, `nzyte-tv-state` contains `u24` and `nzyte-dashboard`, not `nzyte-ops`. The state directory is intended to be `u24:nzyte-tv-state` mode `2750`, with dashboard-consumed state JSON mode `0640`. The production service retains `User=u24`, `Group=u24`, and `UMask=0027`, with `nzyte-tv-state` supplementary membership. Because the base production unit's `StateDirectory` ownership management would otherwise restore the primary `u24` group, the candidate B1 drop-in clears that declaration only after the state directory is explicitly provisioned. The writer's same-directory temporary-file and rename strategy then preserves set-group-ID inheritance and yields group-readable, non-group-writable state files.

The `nzyte-ops` primary group owns the runtime socket directory. The dashboard joins `nzyte-ops` only for socket connection and `nzyte-tv-state` only for runtime-state reads. The helper's systemd write allowlist contains only `/srv/nzyte-tv/media/catalog` and its runtime socket directory. `ReadWritePaths` does not grant Unix discretionary access by itself; the ext4 group ownership and modes must be applied and verified during Pi acceptance. Before enabling mutation, acceptance must also verify socket mode, file inheritance after an actual atomic replacement, bind-mount fail-closed behavior, and systemd hardening on Ubuntu 24.04.

The unit uses:

- `NoNewPrivileges=true` and an empty capability set;
- `ProtectSystem=strict`, `ProtectHome=true`, and explicit read-only state access;
- private temporary and device namespaces;
- kernel, control-group, and SUID/SGID restrictions; and
- explicit denial of the NZYTE TV configuration directory and broadcast-diagnostics document.

`ProtectProc` is intentionally not enabled because the accepted status model checks only the recorded station and FFmpeg PIDs through `Process.GetProcessById`. No command line or `/proc` document is read or exposed.

The templates have no control relationship with or command targeting the broadcaster service. Dashboard startup merely wants the helper; helper failure does not stop the dashboard or broadcaster. Installation and Pi acceptance remain separate operator-guided work.

## Spotlight Record control

The dropdown contains only validated song-catalog entries. A new selection displays the recommended `2.0x` multiplier; accepted values remain `0.1` through `10.0`. Enabling, updating, or disabling Spotlight changes only `ActiveCampaign`. The complete programming configuration is validated and atomically replaced while every unrelated field is preserved.

Each browser mutation supplies the revision it most recently read. A cross-process file lease serializes programming-configuration writes; the helper reloads under that lease and rejects a stale revision with HTTP `409`. The browser then refreshes and asks the operator to review the newer state instead of overwriting it.

Spotlight changes affect newly generated programming only. The active block and already committed future blocks are unchanged.

The dashboard talks to the helper only through `/run/nzyte-tv-operations/operations.sock`. The helper has no TCP listener. In addition to the existing Spotlight operations, B2-C exposes only media summary, refresh, and operation-status calls. Media refresh is disabled unless explicitly configured in the helper; code presence or filesystem discovery never activates it. The browser cannot provide media, inbox, metadata, or catalog paths. If the helper or media storage is unavailable, the affected controls are marked unavailable while the existing status dashboard and broadcaster continue normally.

The Media Operations panel confirms operator intent before requesting a refresh, then polls the accepted operation without holding an HTTP request open. Refresh validates completed READY packages and writes only external metadata generations. It does not normalize media, restart the broadcaster, or rewrite the active or already committed future blocks. New eligible metadata is visible only to a newly captured future planning snapshot, subject to normal catalog, programming, Spotlight, and cooldown rules. Bootstrap is intentionally CLI/admin-only and is not exposed by the dashboard.

No production B2-C activation or service/storage permission change is included in this checkpoint. Those tasks, including the trusted roots and external-mode cutover, belong to B2-D.

## Security boundary

The API DTOs are an allowlist. They omit PIDs, paths, planner/queue/claim identities, internal errors, stack traces, diagnostics, commands, environment data, and secrets. Runtime-state titles and item types are bounded and filtered before projection. Spotlight catalog text is bounded, stripped of control characters, rejected when it resembles a destination or path, and rendered only through `textContent`; Razor output remains HTML encoded. Successful helper mutations log only the operation and revision transition, never paths, titles, raw requests, or exception text.

Responses include a restrictive Content Security Policy, `frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, frame denial, and a no-referrer policy. No external scripts, styles, fonts, or CDNs are used.
