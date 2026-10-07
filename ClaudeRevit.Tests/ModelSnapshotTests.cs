using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class ModelSnapshotTests
{
    private static SnapshotElement E(string id, string type = "Стена 200", string geo = "g1", string? level = "1 этаж", (string, string)[]? p = null) =>
        new(id, id.GetHashCode(), "Стены", type, level, geo, (p ?? [("Марка", "1")]).ToDictionary(x => x.Item1, x => x.Item2));

    [Fact]
    public void DiffFindsAddedDeletedMovedRetypedAndParameterChanges()
    {
        var before = new Snapshot("doc", "Р1", DateTime.UtcNow, [E("a"), E("b"), E("c"), E("d")]);
        var after = new Snapshot("doc", "Р2", DateTime.UtcNow, [E("a"), E("b", type: "Стена 250"), E("c", geo: "g2", level: "2 этаж"), E("d", p: [("Марка", "2")]), E("e")]);
        var diff = ModelSnapshots.Diff(before, after);
        Assert.Equal((1, 0, 1, 1, 1), (diff.Added, diff.Deleted, diff.Moved, diff.Retyped, diff.ParametersChanged));
        Assert.Equal(["Марка"], diff.Changes.Single(c => c.Kind == "parameters").Details);
        Assert.Contains("level 1 этаж → 2 этаж", diff.Changes.Single(c => c.Kind == "moved").Details);
        Assert.Equal(1, diff.ByCategory["Стены"]["added"]);
        var reverse = ModelSnapshots.Diff(after, before);
        Assert.Equal(1, reverse.Deleted);
    }

    [Fact]
    public void SnapshotsRoundTripThroughCompressedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cr-snap-{Guid.NewGuid():N}.json.gz");
        try
        {
            var s = new Snapshot("doc", "Р1", DateTime.UtcNow, [E("a", p: [("Марка", "1"), ("Комментарии", ModelSnapshots.Hash("x"))])]);
            ModelSnapshots.Save(s, path);
            var back = ModelSnapshots.Load(path);
            Assert.Equal(s.Elements[0].Params, back.Elements[0].Params);
            Assert.Equal("Р1", back.Label);
        }
        finally { File.Delete(path); }
    }
}
