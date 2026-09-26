namespace NzyteTv.Core;

public sealed record VerificationCheck(string Section, string Label, bool Passed, string? Detail = null);

public sealed class VerificationResult
{
    public VerificationResult(IEnumerable<VerificationCheck> checks)
    {
        Checks = checks.ToArray();
    }

    public IReadOnlyList<VerificationCheck> Checks { get; }

    public bool IsBroadcastReady => Checks.Count > 0 && Checks.All(check => check.Passed);
}
