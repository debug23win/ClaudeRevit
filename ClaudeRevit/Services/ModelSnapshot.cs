using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeRevit.Services;

// A model state on disk, for "what changed between issue A and issue B". One record per element
// keyed by UniqueId: category, type, level, a geometry key (bounding box rounded to 1 mm) and the
// parameter values (hashed per parameter, so a diff names which parameters changed without storing
// every value). Pure logic: building and diffing snapshots is unit-tested.
public sealed record SnapshotElement(string Id, long ElementId, string Category, string Type, string? Level, string Geo, Dictionary<string, string> Params);
public sealed record Snapshot(string Document, string Label, DateTime Utc, List<SnapshotElement> Elements);

public sealed record SnapshotChange(string Id, long ElementId, string Category, string Kind, List<string> Details);
public sealed record SnapshotDiff(int Added, int Deleted, int Moved, int Retyped, int ParametersChanged, List<SnapshotChange> Changes,
    Dictionary<string, Dictionary<string, int>> ByCategory);

public static class ModelSnapshots
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10];

    public static SnapshotDiff Diff(Snapshot before, Snapshot after)
    {
        var a = before.Elements.ToDictionary(e => e.Id); var b = after.Elements.ToDictionary(e => e.Id);
        var changes = new List<SnapshotChange>();
        foreach (var e in after.Elements.Where(e => !a.ContainsKey(e.Id))) changes.Add(new(e.Id, e.ElementId, e.Category, "added", []));
        foreach (var e in before.Elements.Where(e => !b.ContainsKey(e.Id))) changes.Add(new(e.Id, e.ElementId, e.Category, "deleted", []));
        foreach (var e in after.Elements.Where(e => a.ContainsKey(e.Id)))
        {
            var old = a[e.Id];
            if (old.Type != e.Type) changes.Add(new(e.Id, e.ElementId, e.Category, "retyped", [$"{old.Type} → {e.Type}"]));
            if (old.Geo != e.Geo || old.Level != e.Level) changes.Add(new(e.Id, e.ElementId, e.Category, "moved", old.Level != e.Level ? [$"level {old.Level} → {e.Level}"] : []));
            var names = old.Params.Keys.Union(e.Params.Keys)
                .Where(k => !old.Params.TryGetValue(k, out var x) || !e.Params.TryGetValue(k, out var y) || x != y).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (names.Count > 0) changes.Add(new(e.Id, e.ElementId, e.Category, "parameters", names));
        }
        var byCategory = changes.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.GroupBy(c => c.Kind).ToDictionary(k => k.Key, k => k.Count()));
        int N(string kind) => changes.Count(c => c.Kind == kind);
        return new(N("added"), N("deleted"), N("moved"), N("retyped"), N("parameters"), changes, byCategory);
    }

    public static void Save(Snapshot snapshot, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gz, snapshot);
    }

    public static Snapshot Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Snapshot>(gz) ?? throw new InvalidDataException("Empty snapshot file.");
    }

    public static string Folder(string documentTitle)
    {
        var safe = string.Concat((documentTitle ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit", "snapshots", safe);
    }
}
