using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class StationCompletionEvidenceTests
{
    [Fact]
    public async Task PositiveNonterminalEvidence_IsSealedWithAcceptedCompletedSemantics()
    {
        using var fixture = new CompletionFixture();
        StationRuntimeState positive = fixture.CreatePositiveState();
        await fixture.Store.WriteAsync(fixture.StatePath, positive, CancellationToken.None);
        var service = new StationCompletionEvidenceService(
            fixture.Store,
            processExistence: new FixedProcessExistence());

        StationRuntimeState completed = await service.ConfirmAndSealAsync(
            fixture.StatePath,
            positive.QueueId!,
            positive.QueueItemCount!.Value,
            currentProcessId: 999,
            controlledExecutionFinished: false,
            CancellationToken.None);

        Assert.Equal(StationState.Completed, completed.StationState);
        Assert.Equal(StationBroadcastState.Completed, completed.BroadcastState);
        Assert.Equal(2, completed.LastCompletedGlobalIndex);
        Assert.Null(completed.ResumeGlobalIndex);
        Assert.Null(completed.FfmpegPid);
        Assert.Equal(0, completed.QueuedPlaylistCount);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal(completed, fixture.Store.Read(fixture.StatePath));
    }

    [Fact]
    public async Task IncompleteCursor_IsNeverSealed()
    {
        using var fixture = new CompletionFixture();
        StationRuntimeState incomplete = fixture.CreatePositiveState() with
        {
            CurrentGlobalIndex = 1,
            LastCompletedGlobalIndex = 1,
            ResumeGlobalIndex = 2,
        };
        await fixture.Store.WriteAsync(fixture.StatePath, incomplete, CancellationToken.None);
        var service = new StationCompletionEvidenceService(
            fixture.Store,
            processExistence: new FixedProcessExistence());

        await Assert.ThrowsAsync<RollingStationSafetyException>(() =>
            service.ConfirmAndSealAsync(
                fixture.StatePath,
                incomplete.QueueId!,
                3,
                999,
                false,
                CancellationToken.None));

        Assert.Equal(StationState.Broadcasting, fixture.Store.Read(fixture.StatePath).StationState);
    }

    [Fact]
    public async Task LiveSupervisor_CannotBeSealedExternally()
    {
        using var fixture = new CompletionFixture();
        StationRuntimeState state = fixture.CreatePositiveState();
        await fixture.Store.WriteAsync(fixture.StatePath, state, CancellationToken.None);
        var service = new StationCompletionEvidenceService(
            fixture.Store,
            processExistence: new FixedProcessExistence(state.StationPid));

        RollingStationSafetyException exception = await Assert.ThrowsAsync<RollingStationSafetyException>(() =>
            service.ConfirmAndSealAsync(
                fixture.StatePath,
                state.QueueId!,
                3,
                currentProcessId: 999,
                controlledExecutionFinished: false,
                CancellationToken.None));

        Assert.Contains("live station supervisor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnedInProcessSupervisor_CanBeSealedAfterControlledExecutionFinishes()
    {
        using var fixture = new CompletionFixture();
        StationRuntimeState state = fixture.CreatePositiveState();
        await fixture.Store.WriteAsync(fixture.StatePath, state, CancellationToken.None);
        var service = new StationCompletionEvidenceService(
            fixture.Store,
            processExistence: new FixedProcessExistence(state.StationPid));

        StationRuntimeState completed = await service.ConfirmAndSealAsync(
            fixture.StatePath,
            state.QueueId!,
            3,
            currentProcessId: state.StationPid,
            controlledExecutionFinished: true,
            CancellationToken.None);

        Assert.Equal(StationState.Completed, completed.StationState);
    }

    [Fact]
    public async Task ExclusiveCp2Lock_ProtectsCompletionSealingAndStateIsRereadUnderLock()
    {
        using var fixture = new CompletionFixture();
        StationRuntimeState state = fixture.CreatePositiveState();
        await fixture.Store.WriteAsync(fixture.StatePath, state, CancellationToken.None);
        string lockPath = fixture.StatePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var service = new StationCompletionEvidenceService(
            fixture.Store,
            processExistence: new FixedProcessExistence());

        await Assert.ThrowsAsync<StationStateLockUnavailableException>(() =>
            service.ConfirmAndSealAsync(
                fixture.StatePath,
                state.QueueId!,
                3,
                currentProcessId: 999,
                controlledExecutionFinished: false,
                CancellationToken.None));

        Assert.Equal(StationState.Broadcasting, fixture.Store.Read(fixture.StatePath).StationState);
    }

    [Fact]
    public void CompletionPredicate_BindsSchemaQueueAndItemCountStrictly()
    {
        using var fixture = new CompletionFixture();
        StationRuntimeState state = fixture.CreatePositiveState();
        var service = new StationCompletionEvidenceService(
            fixture.Store,
            processExistence: new FixedProcessExistence());

        Assert.True(service.IsPositiveCompletion(state, state.QueueId!, 3));
        Assert.False(service.IsPositiveCompletion(state, new string('f', 64), 3));
        Assert.False(service.IsPositiveCompletion(state, state.QueueId!, 4));
        Assert.False(service.IsPositiveCompletion(
            state with { SchemaVersion = StationRuntimeState.LegacySchemaVersion },
            state.QueueId!,
            3));
        Assert.False(service.IsPositiveCompletion(
            state with { LastCompletedGlobalIndex = 1, ResumeGlobalIndex = 2 },
            state.QueueId!,
            3));
    }

    private sealed class CompletionFixture : IDisposable
    {
        public CompletionFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-completion-evidence-").FullName;
            StatePath = Path.Combine(Root, "state", "state.json");
            MediaRoot = Directory.CreateDirectory(Path.Combine(Root, "media")).FullName;
            LibraryRoot = Directory.CreateDirectory(Path.Combine(MediaRoot, "library")).FullName;
        }

        public string Root { get; }

        public string StatePath { get; }

        public string MediaRoot { get; }

        public string LibraryRoot { get; }

        public StationStateStore Store { get; } = new();

        public StationRuntimeState CreatePositiveState() => new()
        {
            StationState = StationState.Broadcasting,
            BroadcastState = StationBroadcastState.Broadcasting,
            StationPid = 41,
            StartedAtUtc = DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
            LastHeartbeatUtc = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
            MediaRoot = MediaRoot,
            LibraryRoot = LibraryRoot,
            FfmpegPid = null,
            QueuedPlaylistCount = 0,
            TotalPlaylistCount = 1,
            QueueId = new string('a', 64),
            QueueItemCount = 3,
            CurrentGlobalIndex = 2,
            LastCompletedGlobalIndex = 2,
            ResumeGlobalIndex = null,
            LastStartMode = StationStartMode.Fresh,
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FixedProcessExistence(params int[] livePids) : IProcessExistence
    {
        private readonly HashSet<int> _livePids = [.. livePids];

        public bool Exists(int processId) => _livePids.Contains(processId);
    }
}
