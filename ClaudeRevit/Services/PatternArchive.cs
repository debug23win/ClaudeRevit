using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeRevit.Services;

// Long-term memory for the learning layer. The raw script_journal.jsonl is a bounded ROLLING
// buffer (a few days of heavy use), so patterns derived only from it are forgotten once old
// runs roll off. This archive is the durable side: one deduplicated entry per distinct pattern
// (tool + engine + touched categories), with cumulative run/success counts and a representative
// successful snippet. Distinct patterns grow slowly (hundreds over years, not millions), so this
// stays tiny forever even as raw volume rolls through the journal.
//
// Entries are folded in incrementally: a timestamp watermark means each journal line is counted
// exactly once, ever — folding runs at startup AND right before the journal trims (so nothing is
// evicted from the journal without first being archived).
public static class PatternArchive
{
    // Overridable so tests can point the archive at a temp file. Null → the real %AppData% path.
    internal static string? FilePathOverride;

    private static string FilePath => FilePathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "experience_archive.json");

    // Distinct patterns rarely reach this even over years; the cap is only a runaway backstop.
    private const int MaxPatterns = 1000;

    public sealed class Entry
    {
        public string Tool { get; set; } = "";
        public string? Engine { get; set; }
        public List<string> Categories { get; set; } = new();
        public int Runs { get; set; }
        public int Ok { get; set; }
        public string? SampleCode { get; set; }
        public string? SampleResult { get; set; }
        public string FirstTs { get; set; } = "";
        public string LastTs { get; set; } = "";
    }

    private sealed class File_
    {
        public string LastFoldedTs { get; set; } = "";
        public Dictionary<string, Entry> Patterns { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly object Gate = new();

    public static string LastFoldedTs()
    {
        lock (Gate) { try { return Load().LastFoldedTs; } catch { return ""; } }
    }

    public static List<Entry> Snapshot()
    {
        lock (Gate) { try { return Load().Patterns.Values.ToList(); } catch { return new(); } }
    }

    // Merge journal lines into the archive. Idempotent: a line whose timestamp is at or before the
    // watermark is skipped, so re-folding the same journal (every startup / every trim) never
    // double-counts. Best-effort — never throws into a caller on Revit's shutdown/trim path.
    public static void FoldEntries(IEnumerable<string> jsonLines)
    {
        lock (Gate)
        {
            try
            {
                var file = Load();
                var watermark = file.LastFoldedTs;
                var maxTs = watermark;

                foreach (var line in jsonLines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonElement e;
                    try { using var d = JsonDocument.Parse(line); e = d.RootElement.Clone(); }
                    catch { continue; }

                    var ts = Str(e, "ts") ?? "";
                    if (string.CompareOrdinal(ts, watermark) <= 0) continue; // already folded

                    var tool = Str(e, "tool") ?? "(script)";
                    var engine = Str(e, "engine");
                    var cats = Categories(e);
                    var ok = e.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                    var sig = tool + "|" + (engine ?? "") + "|" + string.Join(",", cats);

                    if (!file.Patterns.TryGetValue(sig, out var p))
                    {
                        p = new Entry { Tool = tool, Engine = engine, Categories = cats, FirstTs = ts };
                        file.Patterns[sig] = p;
                    }
                    p.Runs++;
                    if (ok) p.Ok++;
                    if (ok) // keep the most recent successful snippet as the exemplar
                    {
                        p.SampleCode = Str(e, "code") ?? p.SampleCode;
                        p.SampleResult = Str(e, "result") ?? p.SampleResult;
                    }
                    if (string.CompareOrdinal(ts, p.LastTs) > 0) p.LastTs = ts;
                    if (string.CompareOrdinal(ts, maxTs) > 0) maxTs = ts;
                }

                file.LastFoldedTs = maxTs;
                EnforceCap(file);
                Save(file);
            }
            catch (Exception ex) { Log.Error("PatternArchive.FoldEntries failed", ex); }
        }
    }

    // Drop the least valuable patterns (fewest successes, then oldest) if we somehow exceed the cap.
    private static void EnforceCap(File_ file)
    {
        if (file.Patterns.Count <= MaxPatterns) return;
        var keep = file.Patterns.Values
            .OrderByDescending(p => p.Ok)
            .ThenByDescending(p => p.LastTs, StringComparer.Ordinal)
            .Take(MaxPatterns)
            .ToList();
        file.Patterns = keep.ToDictionary(
            p => p.Tool + "|" + (p.Engine ?? "") + "|" + string.Join(",", p.Categories),
            p => p, StringComparer.Ordinal);
    }

    private static File_ Load()
    {
        if (!File.Exists(FilePath)) return new File_();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<File_>(json) ?? new File_();
        }
        catch { return new File_(); }
    }

    private static void Save(File_ file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Categories(JsonElement e)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (e.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "added_by_category", "modified_by_category" })
                if (ch.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
                    foreach (var prop in obj.EnumerateObject())
                        set.Add(prop.Name);
        return set.ToList();
    }
}
