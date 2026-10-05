using System.Text.Json;
using System.IO;
namespace ClaudeRevit.Services;

public sealed record VerifiedExperience(string RunId, string CodeHash, string? CompleteCode, string Tool, string RevitVersion, string ContextKey, string VerifiedUtc, double Seconds, IReadOnlyList<ObjectiveCheck> Checks, IReadOnlyDictionary<string,string> ElementSignatures)
{
    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Status == "passed");
}
public static class VerifiedExperienceStore
{
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit", "verified_experience.json");
    private static readonly object Gate = new();
    public static IReadOnlyList<VerifiedExperience> Read(string? path = null)
    {
        lock (Gate)
        {
            try { return JsonSerializer.Deserialize<VerifiedExperience[]>(File.ReadAllText(path ?? PathName)) ?? []; }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
        }
    }
    public static IReadOnlyList<VerifiedExperience> Compatible(string revit, string context, string? path = null) => Read(path)
        .Where(r => r.Passed && r.RevitVersion == revit && r.ContextKey == context)
        .OrderByDescending(r => r.VerifiedUtc, StringComparer.Ordinal).Take(20).ToArray();
    public static void Save(VerifiedExperience report, string? path = null)
    {
        lock (Gate)
        {
            path ??= PathName;
            var rows = Read(path).Where(r => r.RunId != report.RunId).Append(report).OrderByDescending(r => r.VerifiedUtc, StringComparer.Ordinal).Take(300).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(rows)); File.Move(tmp, path, true);
        }
    }
    public static string Digest(IEnumerable<VerifiedExperience> rows)
    {
        var passed=rows.Where(r=>r.Passed).Take(8).ToArray();
        return passed.Length==0 ? "" : "OPTIONAL VERIFIED EXPERIENCE INDEX: use get_verified_experience to check current Revit/template applicability and declared checks. Choose a better approach freely; these records do not prove optimality or correctness beyond the checked dimensions.\n" + string.Join("\n",passed.Select(r=>$"{r.Tool}: code SHA256 {r.CodeHash}, Revit {r.RevitVersion}, {r.Checks.Count} checks, {r.Seconds:0.00}s. Full evidence is available on demand."));
    }
}
