namespace ClaudeRevit.Services;

public static class BenchmarkCleanup
{
    public static void ValidateCascade(IReadOnlySet<long> baseline, IReadOnlyCollection<long> added, IReadOnlyCollection<long> deleted)
    {
        if (deleted.Any(baseline.Contains)) throw new InvalidOperationException("Cleanup would delete elements present before the task. Nothing was deleted; restore the test model manually.");
        var cascade = deleted.ToHashSet();
        if (added.Any(id => !cascade.Contains(id))) throw new InvalidOperationException("Cleanup preview does not delete every added element. Nothing was deleted.");
    }
}
