using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class StationResumeTests
{
    private const string QueueId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherQueueId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Decide_NoStateStartsFreshAtGlobalIndexZero()
    {
        StationStartDecision decision = Planner().Decide(null, QueueId, 3);

        Assert.Equal(StationStartMode.Fresh, decision.StartMode);
        Assert.Equal(0, decision.StartItemIndex);
        Assert.Null(decision.LastCompletedGlobalIndex);
        Assert.Equal(0, decision.ResumeCount);
    }

    [Fact]
    public void Decide_SchemaVersionOneNeverSuppliesAColdResumeCursor()
    {
        StationRuntimeState legacy = State(StationState.Broadcasting) with
        {
            SchemaVersion = StationRuntimeState.LegacySchemaVersion,
            QueueId = null,
            QueueItemCount = null,
            CurrentGlobalIndex = null,
            LastCompletedGlobalIndex = null,
            ResumeGlobalIndex = null,
            LastStartMode = null,
        };

        StationStartDecision decision = Planner().Decide(legacy, QueueId, 3);

        Assert.Equal(StationStartMode.Fresh, decision.StartMode);
        Assert.Equal(0, decision.StartItemIndex);
    }

    [Theory]
    [InlineData(StationState.Starting)]
    [InlineData(StationState.Broadcasting)]
    [InlineData(StationState.Stopping)]
    [InlineData(StationState.Failed)]
    [InlineData(StationState.Stopped)]
    public void Decide_MatchingInterruptedOrStoppedStateResumesSavedItem(StationState stationState)
    {
        StationRuntimeState state = State(stationState);

        StationStartDecision decision = Planner().Decide(state, QueueId, 3);

        Assert.Equal(StationStartMode.Resume, decision.StartMode);
        Assert.Equal(1, decision.StartItemIndex);
        Assert.Equal(0, decision.LastCompletedGlobalIndex);
        Assert.Equal(state.ResumeCount + 1, decision.ResumeCount);
    }

    [Fact]
    public void Decide_StoppedStateWithChangedQueueStartsFresh()
    {
        StationStartDecision decision = Planner().Decide(
            State(StationState.Stopped) with { QueueId = OtherQueueId },
            QueueId,
            3);

        Assert.Equal(StationStartMode.Fresh, decision.StartMode);
        Assert.Equal(0, decision.StartItemIndex);
        Assert.Equal(0, decision.ResumeCount);
    }

    [Fact]
    public void Decide_CompletedStateStartsConfiguredQueueFresh()
    {
        StationRuntimeState completed = State(StationState.Completed) with
        {
            CurrentGlobalIndex = 2,
            LastCompletedGlobalIndex = 2,
            ResumeGlobalIndex = null,
        };

        StationStartDecision decision = Planner().Decide(completed, QueueId, 3);

        Assert.Equal(StationStartMode.Fresh, decision.StartMode);
        Assert.Equal(0, decision.StartItemIndex);
    }

    [Fact]
    public void Decide_InterruptedStateWithChangedQueueRefusesAutomaticResume()
    {
        StationStartupException exception = Assert.Throws<StationStartupException>(() =>
            Planner().Decide(
                State(StationState.Broadcasting) with { QueueId = OtherQueueId },
                QueueId,
                3));

        Assert.Contains("different playlist queue", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not attempted", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsafeResumeStates))]
    public void Decide_InconsistentResumeMetadataRefusesToGuess(StationRuntimeState state)
    {
        StationStartupException exception = Assert.Throws<StationStartupException>(() =>
            Planner().Decide(state, QueueId, 3));

        Assert.Contains("unsafe", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not attempted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<StationRuntimeState> UnsafeResumeStates => new()
    {
        State(StationState.Broadcasting) with
        {
            CurrentGlobalIndex = 3,
        },
        State(StationState.Broadcasting) with
        {
            LastCompletedGlobalIndex = 0,
            ResumeGlobalIndex = 2,
        },
        State(StationState.Broadcasting) with
        {
            QueueItemCount = 4,
        },
        State(StationState.Broadcasting) with
        {
            LastCompletedGlobalIndex = 2,
            ResumeGlobalIndex = null,
        },
        State(StationState.Broadcasting) with
        {
            LastStartMode = null,
        },
    };

    [Fact]
    public void Decide_LiveInterruptedStationPidPreventsSecondSupervisor()
    {
        StationRuntimeState state = State(StationState.Broadcasting);

        StationStartupException exception = Assert.Throws<StationStartupException>(() =>
            Planner(state.StationPid).Decide(state, QueueId, 3));

        Assert.Contains(state.StationPid.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("second station supervisor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StationState.Stopped)]
    [InlineData(StationState.Completed)]
    public void Decide_LiveRecordedPidPreventsSecondSupervisorEvenForFinalState(StationState stationState)
    {
        StationRuntimeState state = stationState == StationState.Completed
            ? State(stationState) with
            {
                LastCompletedGlobalIndex = 2,
                ResumeGlobalIndex = null,
            }
            : State(stationState);

        Assert.Throws<StationStartupException>(() =>
            Planner(state.StationPid).Decide(state, QueueId, 3));
    }

    [Fact]
    public void Decide_ReusedPidEqualToCurrentProcessDoesNotLookLikeAnotherSupervisor()
    {
        StationRuntimeState state = State(StationState.Stopped);

        StationStartDecision decision = Planner(state.StationPid).Decide(
            state,
            QueueId,
            3,
            currentProcessId: state.StationPid);

        Assert.Equal(StationStartMode.Resume, decision.StartMode);
    }

    private static StationResumePlanner Planner(params int[] livePids) =>
        new(new FixedProcessExistence(livePids));

    private static StationRuntimeState State(StationState stationState) => new()
    {
        StationState = stationState,
        BroadcastState = StationBroadcastState.Broadcasting,
        StationPid = 41,
        StartedAtUtc = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
        LastHeartbeatUtc = new DateTimeOffset(2026, 9, 29, 12, 1, 0, TimeSpan.Zero),
        MediaRoot = "/media",
        LibraryRoot = "/library",
        TotalPlaylistCount = 1,
        QueuedPlaylistCount = 0,
        QueueId = QueueId,
        QueueItemCount = 3,
        CurrentGlobalIndex = 1,
        LastCompletedGlobalIndex = 0,
        ResumeGlobalIndex = 1,
        LastStartMode = StationStartMode.Fresh,
        ResumeCount = 2,
    };

    private sealed class FixedProcessExistence(IEnumerable<int> livePids) : IProcessExistence
    {
        private readonly HashSet<int> _livePids = [.. livePids];

        public bool Exists(int processId) => _livePids.Contains(processId);
    }
}
