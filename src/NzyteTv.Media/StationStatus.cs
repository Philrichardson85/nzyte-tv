using System.Diagnostics;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IProcessExistence
{
    bool Exists(int processId);
}

public sealed class ProcessExistence : IProcessExistence
{
    public bool Exists(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

public sealed class StationStatusService
{
    private readonly IStationStateStore _stateStore;
    private readonly IProcessExistence _processExistence;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _staleThreshold;

    public StationStatusService(
        IStationStateStore stateStore,
        IProcessExistence processExistence,
        TimeProvider? timeProvider = null,
        TimeSpan? staleThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(processExistence);
        _stateStore = stateStore;
        _processExistence = processExistence;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _staleThreshold = staleThreshold ?? StationRuntimePolicy.StaleHeartbeatThreshold;
        if (_staleThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleThreshold));
        }
    }

    public StationStatusSnapshot GetStatus(string statePath)
    {
        StationRuntimeState state = _stateStore.Read(statePath);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        bool stationExists = _processExistence.Exists(state.StationPid);
        bool ffmpegExists = state.FfmpegPid is int ffmpegPid && _processExistence.Exists(ffmpegPid);
        bool activeState = state.StationState is
            StationState.Starting or StationState.Broadcasting or StationState.Stopping;
        bool heartbeatStale = observedAt - state.LastHeartbeatUtc > _staleThreshold;

        StationStatusKind status = activeState && (heartbeatStale || !stationExists)
            ? StationStatusKind.Stale
            : state.StationState switch
            {
                StationState.Starting => StationStatusKind.Starting,
                StationState.Broadcasting => StationStatusKind.Running,
                StationState.Stopping => StationStatusKind.Stopping,
                StationState.Stopped => StationStatusKind.Stopped,
                StationState.Completed => StationStatusKind.Completed,
                StationState.Failed => StationStatusKind.Failed,
                _ => StationStatusKind.Stale,
            };

        return new StationStatusSnapshot(
            state,
            status,
            stationExists,
            ffmpegExists,
            Directory.Exists(state.MediaRoot),
            Directory.Exists(state.LibraryRoot),
            observedAt);
    }
}
