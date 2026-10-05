using System.Text.Json;
using System.IO;
namespace ClaudeRevit.Services;

public sealed record CalibratedReference(string Task, string Seed, string Environment, string Configuration, string Model, int Samples, double MedianSeconds, double P95Seconds, string ObjectiveVersion);
public static class BenchmarkCalibration
{
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit", "benchmark_calibration.json");
    public static IReadOnlyList<CalibratedReference> Create(IEnumerable<BenchmarkResult> rows) => rows
        .GroupBy(r => $"{r.TaskId}|{r.Model}|{r.SeedFingerprint}|{r.EnvironmentKey}|{r.ComparisonKey}")
        .Where(g => g.Count() >= 5 && g.All(r => r.Score.HasValue && r.Verdict == "✓" && r.Quality >= 90 && r.Objective?.Passed == true && r.Seconds > 0 && double.IsFinite(r.Seconds)))
        .Select(g => { var r = g.First(); var d = BenchmarkStatistics.Summarize(g.Select(x => (x.Seconds, true))); return new CalibratedReference(r.TaskId, r.SeedFingerprint, r.EnvironmentKey, r.ComparisonKey, r.Model, d.Count, d.Median, d.P95, BenchmarkObjective.Version); }).ToArray();
    public static void Save(IEnumerable<BenchmarkResult> rows, string? path = null)
    {
        var references = Create(rows); if (references.Count == 0) throw new InvalidOperationException("Calibration needs at least five comparable passes with quality >=90 and complete objective checks. Failed runs are not silently discarded.");
        path ??= PathName; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(references)); File.Move(tmp, path, true);
    }
    public static (int Seconds, string Source) Resolve(string task, string seed, string environment, string config, int fallback, string? path = null)
    {
        try
        {
            var matches = JsonSerializer.Deserialize<CalibratedReference[]>(File.ReadAllText(path ?? PathName))?.Where(r => r.Task == task && r.Seed == seed && r.Environment == environment && r.Configuration == config && r.ObjectiveVersion == BenchmarkObjective.Version && r.Samples >= 5 && double.IsFinite(r.MedianSeconds) && r.MedianSeconds > 0).ToArray();
            if (matches?.Length == 1) return (Math.Max(1, (int)Math.Ceiling(matches[0].MedianSeconds)), "calibrated median: " + matches[0].Model);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return (fallback, "provisional task reference");
    }
}
