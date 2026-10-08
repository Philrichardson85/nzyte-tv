(() => {
    "use strict";

    const root = document.querySelector(".dashboard");
    if (!root) {
        return;
    }

    const interval = Number.parseInt(root.dataset.refreshIntervalMs || "5000", 10);
    let lastObservedAt = Date.now();

    function setLiteralText(id, value, suffix = "") {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }

        const display = value === null || value === undefined || value === ""
            ? "Unavailable"
            : String(value);
        element.textContent = display + suffix;
    }

    function setStatusText(id, value) {
        const display = value === null || value === undefined || value === ""
            ? value
            : humanize(String(value));
        setLiteralText(id, display);
    }

    function humanize(value) {
        return value
            .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
            .replace(/[_-]+/g, " ")
            .replace(/^./, character => character.toUpperCase());
    }

    function formatTimestamp(value) {
        if (!value) {
            return "Unavailable";
        }

        const date = new Date(value);
        return Number.isNaN(date.valueOf()) ? "Unavailable" : date.toLocaleString();
    }

    function formatDuration(seconds) {
        if (seconds === null || seconds === undefined || seconds < 0) {
            return "Unavailable";
        }

        const whole = Math.floor(seconds);
        const days = Math.floor(whole / 86400);
        const hours = Math.floor((whole % 86400) / 3600);
        const minutes = Math.floor((whole % 3600) / 60);
        const remaining = whole % 60;
        const clock = [hours, minutes, remaining]
            .map(value => String(value).padStart(2, "0"))
            .join(":");
        return days > 0 ? `${days}d ${clock}` : clock;
    }

    function update(snapshot) {
        lastObservedAt = new Date(snapshot.observedAtUtc).valueOf();
        setLiteralText("dashboard-version", snapshot.dashboard.version);
        setStatusText("overall-station", snapshot.station.status);
        setStatusText("snapshot-quality", snapshot.quality);
        setLiteralText("issue-count", snapshot.issues.length);
        setStatusText("station-availability", snapshot.station.availability);
        setStatusText("station-status", snapshot.station.status);
        setStatusText("station-process", snapshot.station.processEvidence);
        setLiteralText("station-heartbeat", formatTimestamp(snapshot.station.heartbeatAtUtc));
        setLiteralText("heartbeat-age", snapshot.station.heartbeatAgeSeconds,
            snapshot.station.heartbeatAgeSeconds === null ? "" : " seconds");
        setLiteralText("session-uptime", formatDuration(snapshot.station.sessionUptimeSeconds));
        setStatusText("broadcast-state", snapshot.broadcast.state);
        setStatusText("broadcast-local-state", snapshot.broadcast.state);
        setStatusText("ffmpeg-state", snapshot.broadcast.ffmpegState);
        setLiteralText("recovery-attempts", snapshot.broadcast.recoveryAttempts);
        setStatusText("playback-availability", snapshot.playback.availability);
        setLiteralText("playback-title", snapshot.playback.title);
        setStatusText("playback-type", snapshot.playback.itemType);
        const queue = snapshot.playback.currentItemNumber !== null
            && snapshot.playback.currentItemNumber !== undefined
            && snapshot.playback.totalItemCount !== null
            && snapshot.playback.totalItemCount !== undefined
            ? `${snapshot.playback.currentItemNumber} of ${snapshot.playback.totalItemCount}`
            : null;
        setLiteralText("queue-position", queue);
        setStatusText("rolling-availability", snapshot.rolling.availability);
        setStatusText("rolling-phase", snapshot.rolling.phase);
        setLiteralText("active-block", snapshot.rolling.activeBlockSequence);
        setLiteralText("last-completed-block", snapshot.rolling.lastCompletedBlockSequence);
        setLiteralText("next-required-block", snapshot.rolling.nextRequiredBlockSequence);
        setLiteralText("future-block-count", snapshot.rolling.committedFutureBlockCount);
        setLiteralText("future-block-target", snapshot.rolling.futureBlockTarget);
        setLiteralText("buffer-deficit", snapshot.rolling.bufferDeficit);
        setStatusText("buffer-health", snapshot.rolling.bufferHealth);
        setStatusText("replenishment-health", snapshot.rolling.replenishmentHealth);
        setLiteralText("last-refreshed", formatTimestamp(snapshot.observedAtUtc));
        setLiteralText("refresh-state", "Automatic refresh active");

        const overall = document.getElementById("overall-status");
        if (overall) {
            overall.dataset.stationState = snapshot.station.status;
        }
    }

    function updateAge() {
        const age = Math.max(0, Math.floor((Date.now() - lastObservedAt) / 1000));
        setLiteralText("snapshot-age", age, "s");
    }

    async function poll() {
        try {
            const response = await fetch("/api/v1/status", {
                cache: "no-store",
                headers: { "Accept": "application/json" }
            });
            if (!response.ok) {
                throw new Error("status request failed");
            }

            update(await response.json());
        } catch {
            setLiteralText("refresh-state", "Refresh failed — showing last known snapshot");
            setStatusText("snapshot-quality", "degraded");
        } finally {
            window.setTimeout(poll, interval);
        }
    }

    updateAge();
    window.setInterval(updateAge, 1000);
    window.setTimeout(poll, interval);
})();
