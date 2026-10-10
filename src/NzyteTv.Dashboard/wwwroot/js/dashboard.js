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
    let mediaRevision = null;
    let mediaOperationId = null;
    let mediaPollTimer = null;
    let mediaSummaryTimer = null;

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

    function setMediaButton(enabled) {
        const button = document.getElementById("media-refresh");
        if (button) button.disabled = !enabled;
    }

    function renderMediaUnavailable(message = "Media update is unavailable. Broadcast status is unaffected.") {
        mediaRevision = null;
        setStatusText("media-availability", "unavailable");
        setLiteralText("media-revision", null);
        setLiteralText("media-generation", null);
        setStatusText("media-refresh-status", "unavailable");
        setLiteralText("media-message", message);
        setMediaButton(false);
    }

    function renderMediaSummary(summary) {
        const state = summary.featureState;
        mediaRevision = summary.metadataRevision;
        setStatusText("media-availability", state);
        setLiteralText("media-revision", summary.metadataRevision);
        setLiteralText("media-generation", summary.generationId);
        setStatusText("media-refresh-status", state === "busy" ? "running" : state);
        if (state === "disabled") {
            setLiteralText("media-message", "Media update is not enabled on this installation.");
            setMediaButton(false);
        } else if (state === "unavailable") {
            renderMediaUnavailable();
        } else if (state === "busy") {
            setLiteralText("media-message", "Media-library refresh is in progress.");
            setMediaButton(false);
            if (summary.activeOperationId) beginMediaPolling(summary.activeOperationId);
        } else {
            setLiteralText("media-message", "Completed READY packages can be checked for future programming.");
            setMediaButton(Number.isInteger(summary.metadataRevision) && summary.metadataRevision > 0);
        }
        if (summary.lastOperation && !mediaOperationId) renderMediaOperation(summary.lastOperation);
    }

    async function refreshMediaSummary(scheduleNext = true) {
        if (mediaSummaryTimer !== null) {
            window.clearTimeout(mediaSummaryTimer);
            mediaSummaryTimer = null;
        }
        try {
            const response = await fetch("/api/v1/media-library", {
                cache: "no-store",
                headers: { "Accept": "application/json" }
            });
            if (!response.ok) throw new Error("media summary request failed");
            renderMediaSummary(await response.json());
        } catch {
            renderMediaUnavailable();
        } finally {
            if (scheduleNext && mediaOperationId === null) {
                mediaSummaryTimer = window.setTimeout(refreshMediaSummary, interval);
            }
        }
    }

    const mediaIssueMessages = {
        ambiguousCatalogMatch: "A song has more than one catalog match.",
        catalogMatchRequired: "A song requires catalog maintenance before it can become eligible.",
        incompletePackage: "A READY package is incomplete.",
        packageIdConflict: "A package identifier conflicts with previously accepted content.",
        existingMediaConflict: "A package targets media already accepted by another package.",
        refreshFailed: "The refresh could not be completed safely.",
        catalogChanged: "The song catalog changed during refresh; no mixed snapshot was published."
    };

    function renderMediaOperation(operation) {
        const results = document.getElementById("media-results");
        if (results) results.hidden = false;
        setStatusText("media-refresh-status", operation.status);
        setLiteralText("media-packages-observed", operation.packagesObserved);
        setLiteralText("media-package-outcomes",
            `${operation.packagesAccepted} / ${operation.packagesAlreadyProcessed} / ${operation.packagesRejected}`);
        setLiteralText("media-new-assets", `${operation.newSourceAssets} / ${operation.newLibraryAssets}`);
        setLiteralText("media-metadata-counts",
            `${operation.metadataRecordsCreated} / ${operation.metadataRecordsPreserved}`);
        setLiteralText("media-newly-eligible", operation.assetsNewlyEligible);
        setLiteralText("media-attention-counts",
            `${operation.unresolvedAssets} / ${operation.incompletePackages} / ${operation.skippedPackages}`);
        setLiteralText("media-warning-error-counts", `${operation.warningCount} / ${operation.errorCount}`);
        setLiteralText("media-revision-change",
            `${operation.metadataRevisionBefore} → ${operation.metadataRevisionAfter}`);

        const issues = document.getElementById("media-issues");
        if (issues) {
            issues.replaceChildren();
            const grouped = new Map();
            for (const issue of operation.issues || []) {
                grouped.set(issue.code, (grouped.get(issue.code) || 0) + 1);
            }
            for (const [code, count] of grouped) {
                const item = document.createElement("li");
                item.textContent = `${mediaIssueMessages[code] || "A package needs operator attention."} (${count})`;
                issues.appendChild(item);
            }
        }

        if (operation.status === "noChanges") {
            setLiteralText("media-message", "No new eligible READY packages were published.");
        } else if (operation.status === "succeededWithWarnings") {
            setLiteralText("media-message", "Refresh completed with items that need attention.");
        } else if (operation.status === "succeeded") {
            setLiteralText("media-message", "Refresh completed. New metadata is available to future planning.");
        } else if (operation.status === "failed") {
            setLiteralText("media-message", "Refresh failed safely. The prior metadata generation remains authoritative.");
        } else if (operation.status === "interrupted") {
            setLiteralText("media-message", "Refresh was interrupted. It was not restarted automatically.");
        }
    }

    function isTerminalMediaStatus(status) {
        return ["succeeded", "succeededWithWarnings", "noChanges", "failed", "interrupted"].includes(status);
    }

    function beginMediaPolling(operationId) {
        if (!operationId || mediaOperationId === operationId) return;
        mediaOperationId = operationId;
        if (mediaSummaryTimer !== null) window.clearTimeout(mediaSummaryTimer);
        if (mediaPollTimer !== null) window.clearTimeout(mediaPollTimer);
        setMediaButton(false);
        const pollOperation = async () => {
            try {
                const response = await fetch(`/api/v1/media-library/operations/${encodeURIComponent(operationId)}`, {
                    cache: "no-store",
                    headers: { "Accept": "application/json" }
                });
                if (!response.ok) throw new Error("media operation request failed");
                const operation = await response.json();
                renderMediaOperation(operation);
                if (isTerminalMediaStatus(operation.status)) {
                    mediaOperationId = null;
                    await refreshMediaSummary();
                    return;
                }
                mediaPollTimer = window.setTimeout(pollOperation, 1500);
            } catch {
                mediaOperationId = null;
                renderMediaUnavailable("Media update status is unavailable. The operation was not restarted.");
                mediaSummaryTimer = window.setTimeout(refreshMediaSummary, interval);
            }
        };
        mediaPollTimer = window.setTimeout(pollOperation, 1000);
    }

    async function startMediaRefresh() {
        if (!Number.isInteger(mediaRevision) || mediaOperationId !== null) return;
        if (!window.confirm(
            "Update Media Library will validate completed READY packages and publish eligible metadata for future programming. It will not normalize media or rewrite active/committed blocks.")) {
            return;
        }
        setMediaButton(false);
        setLiteralText("media-message", "Starting media-library refresh…");
        try {
            const response = await fetch("/api/v1/media-library/refresh", {
                method: "POST",
                cache: "no-store",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json",
                    "X-NZYTE-TV-CSRF": antiforgeryToken
                },
                body: JSON.stringify({
                    schemaVersion: 1,
                    expectedMetadataRevision: mediaRevision
                })
            });
            if (response.status === 409) {
                await refreshMediaSummary();
                setLiteralText("media-message",
                    "Media metadata changed or another refresh started. Review the refreshed state before trying again.");
                return;
            }
            if (!response.ok) throw new Error("media refresh request failed");
            const accepted = await response.json();
            setLiteralText("media-message", "Media-library refresh is in progress.");
            beginMediaPolling(accepted.operationId);
        } catch {
            await refreshMediaSummary();
            setLiteralText("media-message", "Media update could not be started. Review the current state and try again.");
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

    const mediaRefreshButton = document.getElementById("media-refresh");
    if (mediaRefreshButton) mediaRefreshButton.addEventListener("click", startMediaRefresh);

    updateAge();
    window.setInterval(updateAge, 1000);
    window.setTimeout(poll, interval);
    refreshSpotlight();
    refreshMediaSummary();
})();
