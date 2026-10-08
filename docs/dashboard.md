# Read-only web dashboard (Checkpoint 3B3-A)

Checkpoint 3B3-A adds a separate ASP.NET Core operations dashboard. It does not run inside the broadcaster, invoke FFmpeg, call systemd, or provide station controls. Restarting or stopping the dashboard does not stop the broadcaster.

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

There are no operational POST, PUT, PATCH, or DELETE endpoints. There are no start, stop, restart, reconnect, planner, playlist, upload, delete, shell, or systemd controls.

The browser polls the status endpoint every five seconds. The server maintains one short-lived in-memory snapshot, so browser polling does not independently read every state file. A refresh failure produces a safe unavailable snapshot and does not terminate the web process.

## Binding and remote operator access

The application configures exactly one Kestrel listener on IPv4 loopback. The default endpoint is:

```text
http://127.0.0.1:5080
```

Only `Dashboard:Port` is supported for listener configuration. Alternative ASP.NET Core URL and listener settings—including `urls`, HTTP/HTTPS port settings, and `Kestrel:Endpoints`—cause startup to fail closed. This applies in Production, Testing, and every other environment; `ASPNETCORE_URLS`, `DOTNET_URLS`, command-line `--urls`, and Kestrel endpoint environment settings cannot enable wildcard or remote binding. Direct LAN and public binding are not supported in this checkpoint and require later authentication, HTTPS, and firewall design.

Use an SSH tunnel from the operator workstation:

```bash
ssh -L 5080:127.0.0.1:5080 u24@PI_HOSTNAME_OR_ADDRESS
```

Keep that SSH session open and browse to `http://127.0.0.1:5080` on the workstation. The HTTP connection remains inside the SSH tunnel.

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

All state paths must be absolute and distinct. They are process configuration only; the browser cannot supply paths. Windows development defaults to a `dashboard-state` directory beside the executable unless tests or local configuration provide temporary absolute paths.

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

## Independent systemd template

The candidate unit is [nzyte-tv-dashboard.service](../deploy/checkpoint-3b3-a/nzyte-tv-dashboard.service). It is separate from `nzyte-tv.service`, runs as `u24` to read the existing mode-`0750` state directory, and does not load `/etc/nzyte-tv/secrets.env`.

The unit uses:

- `NoNewPrivileges=true` and an empty capability set;
- `ProtectSystem=strict`, `ProtectHome=true`, and explicit read-only state access;
- private temporary and device namespaces;
- kernel, control-group, and SUID/SGID restrictions; and
- explicit denial of the NZYTE TV configuration directory and broadcast-diagnostics document.

`ProtectProc` is intentionally not enabled because the accepted status model checks only the recorded station and FFmpeg PIDs through `Process.GetProcessById`. No command line or `/proc` document is read or exposed.

The template has no dependency on, control relationship with, or command targeting the broadcaster service. Installation and Pi acceptance remain separate operator-guided work.

## Security boundary

The API DTOs are an allowlist. They omit PIDs, paths, planner/queue/claim identities, internal errors, stack traces, diagnostics, commands, environment data, and secrets. Titles and item types are bounded, stripped of control/markup characters, and rejected when they resemble paths or command lines. Browser updates use `textContent`, and Razor output remains HTML encoded.

Responses include a restrictive Content Security Policy, `frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, frame denial, and a no-referrer policy. No external scripts, styles, fonts, or CDNs are used.
