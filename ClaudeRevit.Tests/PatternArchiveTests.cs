using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

// Tests the durable pattern archive (the long-term half of the learning layer): folding journal
// lines must count runs/successes, dedup by pattern signature, skip already-folded entries by the
// timestamp watermark (so re-folding never double-counts), and record failures. Pure logic — no
// Revit — so it runs in CI on any OS.
public class PatternArchiveTests : IDisposable
{
    private readonly string _tmp;

    public PatternArchiveTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "cr_archive_test_" + Guid.NewGuid().ToString("N") + ".json");
        PatternArchive.FilePathOverride = _tmp;
    }

    public void Dispose()
    {
        PatternArchive.FilePathOverride = null;
        try { if (File.Exists(_tmp)) File.Delete(_tmp); } catch { }
    }

    // Build a journal line like ScriptJournal writes: ts, tool, engine, ok, changes.added_by_category.
    private static string Line(string ts, string tool, bool ok, params string[] categories)
    {
        var cats = categories.Length == 0
            ? ""
            : ",\"changes\":{\"added_by_category\":{" +
              string.Join(",", categories.Select(c => $"\"{c}\":1")) + "}}";
        return $"{{\"ts\":\"{ts}\",\"tool\":\"{tool}\",\"engine\":null,\"ok\":{(ok ? "true" : "false")}{cats}}}";
    }

    [Fact]
    public void Fold_counts_runs_and_successes_and_dedups_by_pattern()
    {
        var lines = new List<string>
        {
            Line("2026-07-05T01:00:00Z", "execute_csharp", true,  "Walls"),
            Line("2026-07-05T01:00:01Z", "execute_csharp", true,  "Walls"),   // same pattern
            Line("2026-07-05T01:00:02Z", "execute_csharp", false, "Walls"),   // same pattern, failed
            Line("2026-07-05T01:00:03Z", "execute_csharp", true,  "Floors"),  // different pattern
        };

        PatternArchive.FoldEntries(lines);
        var patterns = PatternArchive.Snapshot();

        Assert.Equal(2, patterns.Count); // Walls and Floors — deduped
        var walls = patterns.Single(p => p.Categories.SequenceEqual(new[] { "Walls" }));
        Assert.Equal(3, walls.Runs);
        Assert.Equal(2, walls.Ok); // two succeeded, one failed
        var floors = patterns.Single(p => p.Categories.SequenceEqual(new[] { "Floors" }));
        Assert.Equal(1, floors.Runs);
        Assert.Equal(1, floors.Ok);
    }

    [Fact]
    public void Fold_is_idempotent_via_watermark()
    {
        var lines = new List<string>
        {
            Line("2026-07-05T02:00:00Z", "execute_csharp", true, "Rebar"),
            Line("2026-07-05T02:00:01Z", "execute_csharp", true, "Rebar"),
        };

        PatternArchive.FoldEntries(lines);
        PatternArchive.FoldEntries(lines); // re-fold the SAME lines — must not double count
        PatternArchive.FoldEntries(lines);

        var rebar = PatternArchive.Snapshot().Single();
        Assert.Equal(2, rebar.Runs);
        Assert.Equal("2026-07-05T02:00:01Z", PatternArchive.LastFoldedTs());
    }

    [Fact]
    public void Fold_skips_entries_at_or_before_the_watermark()
    {
        PatternArchive.FoldEntries(new[] { Line("2026-07-05T03:00:05Z", "execute_csharp", true, "Grids") });

        // An OLDER entry (ts before the watermark) must be ignored; a NEWER one must be counted.
        PatternArchive.FoldEntries(new[]
        {
            Line("2026-07-05T03:00:01Z", "execute_csharp", true, "Grids"), // older → skipped
            Line("2026-07-05T03:00:09Z", "execute_csharp", true, "Grids"), // newer → counted
        });

        var grids = PatternArchive.Snapshot().Single();
        Assert.Equal(2, grids.Runs); // the first + the newer one; the older one skipped
    }

    [Fact]
    public void Fold_keeps_a_successful_sample_and_first_last_timestamps()
    {
        PatternArchive.FoldEntries(new[]
        {
            Line("2026-07-05T04:00:00Z", "execute_csharp", true, "Levels"),
            Line("2026-07-05T04:05:00Z", "execute_csharp", true, "Levels"),
        });

        var p = PatternArchive.Snapshot().Single();
        Assert.Equal("2026-07-05T04:00:00Z", p.FirstTs);
        Assert.Equal("2026-07-05T04:05:00Z", p.LastTs);
    }

    [Fact]
    public void Empty_and_malformed_lines_are_ignored()
    {
        PatternArchive.FoldEntries(new[] { "", "   ", "{not json", Line("2026-07-05T05:00:00Z", "execute_csharp", true, "Rooms") });
        var p = PatternArchive.Snapshot().Single();
        Assert.Equal(1, p.Runs);
    }
}
