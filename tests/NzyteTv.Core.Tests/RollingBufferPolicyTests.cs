using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class RollingBufferPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

    [Theory]
    [InlineData(0, 1, 3, 3, 0, RollingBufferHealth.Empty)]
    [InlineData(1, 1, 3, 2, 0, RollingBufferHealth.Empty)]
    [InlineData(2, 1, 3, 1, 1, RollingBufferHealth.Low)]
    [InlineData(3, 1, 3, 0, 2, RollingBufferHealth.Healthy)]
    public void Uninitialized_UsesActivationCandidatePlusTwoFutureBlocks(
        int committed,
        long expectedAnchor,
        long expectedRequired,
        long expectedDeficit,
        long expectedFuture,
        RollingBufferHealth expectedHealth)
    {
        RollingBufferSnapshot result = RollingBufferPolicy.Calculate(
            CreateManifest(committed),
            executionState: null);

        Assert.Equal(1, result.NextRequiredSequence);
        Assert.Equal(expectedAnchor, result.AnchorSequence);
        Assert.Equal(expectedRequired, result.RequiredHighestSequence);
        Assert.Equal(expectedDeficit, result.BufferDeficit);
        Assert.Equal(expectedFuture, result.CommittedFutureBlockCount);
        Assert.Equal(expectedHealth, result.Health);
    }

    [Theory]
    [InlineData(RollingStationPhase.Claimed)]
    [InlineData(RollingStationPhase.Executing)]
    [InlineData(RollingStationPhase.Stopped)]
    [InlineData(RollingStationPhase.Failed)]
    public void ActivePhases_AnchorOnActiveSequence(RollingStationPhase phase)
    {
        RollingStationRuntimeState state = CreateActiveState(phase, active: 2, completed: 1);

        RollingBufferSnapshot result = RollingBufferPolicy.Calculate(CreateManifest(3), state);

        Assert.Equal(2, result.NextRequiredSequence);
        Assert.Equal(2, result.AnchorSequence);
        Assert.Equal(4, result.RequiredHighestSequence);
        Assert.Equal(1, result.BufferDeficit);
        Assert.Equal(1, result.CommittedFutureBlockCount);
        Assert.Equal(RollingBufferHealth.Low, result.Health);
    }

    [Theory]
    [InlineData(RollingStationPhase.Advancing)]
    [InlineData(RollingStationPhase.WaitingForBlock)]
    [InlineData(RollingStationPhase.Failed)]
    public void PhasesWithoutActiveClaim_AnchorOnNextRequired(RollingStationPhase phase)
    {
        RollingStationRuntimeState state = CreateInactiveState(phase, completed: 3);

        RollingBufferSnapshot result = RollingBufferPolicy.Calculate(CreateManifest(3), state);

        Assert.Equal(4, result.NextRequiredSequence);
        Assert.Equal(4, result.AnchorSequence);
        Assert.Equal(6, result.RequiredHighestSequence);
        Assert.Equal(3, result.BufferDeficit);
        Assert.Equal(RollingBufferHealth.Empty, result.Health);
    }

    [Fact]
    public void ActiveBlockThree_RequiresCommitmentThroughFive()
    {
        RollingBufferSnapshot result = RollingBufferPolicy.Calculate(
            CreateManifest(4),
            CreateActiveState(RollingStationPhase.Executing, active: 3, completed: 2));

        Assert.Equal(5, result.RequiredHighestSequence);
        Assert.Equal(1, result.BufferDeficit);
    }

    [Fact]
    public void PartialActiveIdentity_IsRejectedRatherThanGuessed()
    {
        RollingStationRuntimeState state = CreateInactiveState(
            RollingStationPhase.Failed,
            completed: null) with
        {
            ActiveBlockSequence = 1,
        };

        Assert.Throws<InvalidDataException>(() =>
            RollingBufferPolicy.Calculate(CreateManifest(3), state));
    }

    [Fact]
    public void PhaseThatRequiresClaimWithoutClaim_IsRejected()
    {
        RollingStationRuntimeState state = CreateInactiveState(
            RollingStationPhase.Executing,
            completed: null);

        Assert.Throws<InvalidDataException>(() =>
            RollingBufferPolicy.Calculate(CreateManifest(3), state));
    }

    [Fact]
    public void ActiveClaimOutsideCommittedPrefix_IsRejected()
    {
        RollingStationRuntimeState state = CreateActiveState(
            RollingStationPhase.Claimed,
            active: 4,
            completed: 3);

        Assert.Throws<InvalidDataException>(() =>
            RollingBufferPolicy.Calculate(CreateManifest(3), state));
    }

    [Fact]
    public void NoncontiguousManifest_IsRejected()
    {
        RollingProgrammingManifest manifest = CreateManifest(2);
        manifest = new RollingProgrammingManifest
        {
            PlannerId = manifest.PlannerId,
            TargetPreparedBlockCount = 3,
            NextSequence = 3,
            Blocks = [new RollingCommittedBlock { Sequence = 1 }, new RollingCommittedBlock { Sequence = 3 }],
        };

        Assert.Throws<InvalidDataException>(() =>
            RollingBufferPolicy.Calculate(manifest, null));
    }

    private static RollingProgrammingManifest CreateManifest(int count) => new()
    {
        PlannerId = "00112233445566778899aabbccddeeff",
        TargetPreparedBlockCount = 3,
        NextSequence = count + 1,
        Blocks = Enumerable.Range(1, count)
            .Select(sequence => new RollingCommittedBlock { Sequence = sequence })
            .ToArray(),
    };

    private static RollingStationRuntimeState CreateActiveState(
        RollingStationPhase phase,
        long active,
        long? completed) => new()
        {
            PlannerId = "00112233445566778899aabbccddeeff",
            Phase = phase,
            ActiveBlockSequence = active,
            ActiveBlockId = new string('a', 64),
            ActiveQueueId = new string('b', 64),
            ClaimedAtUtc = Now,
            LastCompletedBlockSequence = completed,
            LastCompletedBlockId = completed is null ? null : new string('c', 64),
            LastCompletedAtUtc = completed is null ? null : Now - TimeSpan.FromHours(6),
            UpdatedAtUtc = Now,
            FailureDisposition = phase == RollingStationPhase.Failed
            ? RollingStationFailureDisposition.Restartable
            : null,
            LastTransitionError = phase == RollingStationPhase.Failed ? "temporary" : null,
        };

    private static RollingStationRuntimeState CreateInactiveState(
        RollingStationPhase phase,
        long? completed) => new()
        {
            PlannerId = "00112233445566778899aabbccddeeff",
            Phase = phase,
            LastCompletedBlockSequence = completed,
            LastCompletedBlockId = completed is null ? null : new string('c', 64),
            LastCompletedAtUtc = completed is null ? null : Now - TimeSpan.FromHours(6),
            UpdatedAtUtc = Now,
            FailureDisposition = phase == RollingStationPhase.Failed
            ? RollingStationFailureDisposition.Restartable
            : null,
            LastTransitionError = phase == RollingStationPhase.Failed ? "temporary" : null,
        };
}
