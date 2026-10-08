(() => {
    "use strict";

    const root = document.querySelector(".dashboard");
    if (!root) {
        return;
    }

    const interval = Number.parseInt(root.dataset.refreshIntervalMs || "5000", 10);
    const antiforgeryToken = root.dataset.antiforgeryToken || "";
    let lastObservedAt = Date.now();
    let spotlightRevision = null;
    let spotlightBusy = false;

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

    function setSpotlightControlsEnabled(enabled, spotlightEnabled = false) {
        const song = document.getElementById("spotlight-song");
        const weight = document.getElementById("spotlight-weight");
        const setButton = document.getElementById("spotlight-set");
        const disableButton = document.getElementById("spotlight-disable");
        if (song) song.disabled = !enabled;
        if (weight) weight.disabled = !enabled;
        if (setButton) setButton.disabled = !enabled;
        if (disableButton) disableButton.disabled = !enabled || !spotlightEnabled;
    }

    function renderSpotlight(state) {
        spotlightRevision = state.revision;
        setStatusText("spotlight-availability", "available");
        setLiteralText("spotlight-current", state.enabled
            ? `${state.artist} — ${state.title}`
            : "Disabled");
        setLiteralText("spotlight-current-weight", state.enabled
            ? `${state.weightMultiplier.toFixed(1)}x`
            : "Unavailable");

        const select = document.getElementById("spotlight-song");
        if (select) {
            select.replaceChildren();
            for (const song of state.catalogOptions) {
                const option = document.createElement("option");
                option.value = song.contentGroupId;
                option.textContent = `${song.artist} — ${song.title}`;
                option.selected = song.contentGroupId === state.contentGroupId;
                select.appendChild(option);
            }
        }

        const weight = document.getElementById("spotlight-weight");
        if (weight) weight.value = state.enabled ? state.weightMultiplier.toFixed(1) : "2.0";
        setSpotlightControlsEnabled(state.catalogOptions.length > 0, state.enabled);
    }

    function renderSpotlightUnavailable() {
        spotlightRevision = null;
        setStatusText("spotlight-availability", "unavailable");
        setLiteralText("spotlight-current", "Unavailable");
        setLiteralText("spotlight-current-weight", null);
        setSpotlightControlsEnabled(false);
    }

    async function refreshSpotlight(showFailure = true) {
        try {
            const response = await fetch("/api/v1/programming/spotlight", {
                cache: "no-store",
                headers: { "Accept": "application/json" }
            });
            if (!response.ok) throw new Error("spotlight request failed");
            renderSpotlight(await response.json());
            if (showFailure) setLiteralText("spotlight-message", "Spotlight controls ready.");
            return true;
        } catch {
            renderSpotlightUnavailable();
            if (showFailure) {
                setLiteralText("spotlight-message",
                    "Programming controls are unavailable. Broadcast status is unaffected.");
            }
            return false;
        }
    }

    async function mutateSpotlight(path, body) {
        if (spotlightBusy || spotlightRevision === null) return;
        spotlightBusy = true;
        setSpotlightControlsEnabled(false);
        setLiteralText("spotlight-message", "Applying Spotlight change…");
        try {
            const response = await fetch(path, {
                method: "POST",
                cache: "no-store",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json",
                    "X-NZYTE-TV-CSRF": antiforgeryToken
                },
                body: JSON.stringify(body)
            });
            if (response.status === 409) {
                await refreshSpotlight(false);
                setLiteralText("spotlight-message",
                    "Spotlight changed elsewhere. Review the refreshed selection before trying again.");
                return;
            }
            if (!response.ok) throw new Error("spotlight mutation failed");
            renderSpotlight(await response.json());
            setLiteralText("spotlight-message", "Spotlight change saved.");
        } catch {
            await refreshSpotlight(false);
            setLiteralText("spotlight-message",
                "Spotlight change was not saved. Review the current state and try again.");
        } finally {
            spotlightBusy = false;
        }
    }

    const setSpotlightButton = document.getElementById("spotlight-set");
    if (setSpotlightButton) {
        setSpotlightButton.addEventListener("click", () => {
            const song = document.getElementById("spotlight-song");
            const weight = document.getElementById("spotlight-weight");
            mutateSpotlight("/api/v1/programming/spotlight", {
                expectedRevision: spotlightRevision,
                contentGroupId: song.value,
                weightMultiplier: Number.parseFloat(weight.value)
            });
        });
    }

    const disableSpotlightButton = document.getElementById("spotlight-disable");
    if (disableSpotlightButton) {
        disableSpotlightButton.addEventListener("click", () => mutateSpotlight(
            "/api/v1/programming/spotlight/disable",
            { expectedRevision: spotlightRevision }));
    }

    updateAge();
    window.setInterval(updateAge, 1000);
    window.setTimeout(poll, interval);
    refreshSpotlight();
})();
