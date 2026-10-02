namespace ClaudeRevit.Services;

public sealed record BenchmarkPoints(int Quality, double Speed, double Total);

public static class BenchmarkScoring
{
    public const string Version = "quality-speed-v1";
    // A quick incorrect result earns no speed points on its own. Reference times are fixed
    // per task, not derived from the other models in a run, nor from the cancellation budget.
    public static BenchmarkPoints? Calculate(BenchmarkVerdict verdict, double seconds, double referenceSeconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(referenceSeconds) || referenceSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Timing must be finite and reference time positive.");
        if (!verdict.Graded) return null;
        var quality = Math.Clamp(verdict.Score, 0, 100);
        var speed = 100 * Math.Min(1, referenceSeconds / Math.Max(seconds, 0.001));
        return new(quality, Math.Round(speed, 1), Math.Round(quality * (0.8 + 0.2 * speed / 100), 1));
    }
}
