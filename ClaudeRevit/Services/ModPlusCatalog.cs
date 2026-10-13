using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace ClaudeRevit.Services;

// Metadata ships with ClaudeRevit; decompiled sources stay in the user's local analysis bundle.
// Nothing in this catalog is compiled, loaded as an assembly, or used as an executable argument.
public static class ModPlusCatalog
{
    private static readonly Lazy<JsonElement> Bundled = new(() =>
        Parse(ReadEmbedded(Assembly.GetExecutingAssembly(), "ClaudeRevit.Standards.ModPlus2027.json")
            ?? throw new InvalidOperationException("Bundled ModPlus catalog is missing.")));

    // The add-in embeds the large ModPlus snapshots gzipped as "<name>.gz"; the isolated test
    // projects embed them uncompressed under the plain name. Null when neither is present.
    public static string? ReadEmbedded(Assembly assembly, string name) => ReadEmbedded(assembly.GetManifestResourceStream, name);

    public static string? ReadEmbedded(Func<string, Stream?> open, string name)
    {
        using var compressed = open(name + ".gz");
        using var stream = compressed != null ? new GZipStream(compressed, CompressionMode.Decompress) : open(name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static readonly object Gate = new();
    private static string? _cachedFile;
    private static DateTime _cachedWrite;
    private static long _cachedLength;
    private static JsonElement _cached;

    public static string DataDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("CLAUDEREVIT_MODPLUS_DATA") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit", "tools", "modplus-revit-2027"));

    public static JsonElement Source
    {
        get
        {
            var file = new FileInfo(Path.Combine(DataDirectory, "catalog.json"));
            if (!file.Exists) return Bundled.Value;
            lock (Gate)
            {
                if (_cachedFile == file.FullName && _cachedWrite == file.LastWriteTimeUtc && _cachedLength == file.Length) return _cached;
                if (file.Length > 8_000_000) throw new InvalidDataException("ModPlus catalog exceeds 8 MB.");
                var parsed = Parse(File.ReadAllText(file.FullName));
                _cached = parsed; _cachedFile = file.FullName; _cachedWrite = file.LastWriteTimeUtc; _cachedLength = file.Length;
                return parsed;
            }
        }
    }

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("targetRevit").GetInt32() != 2027)
            throw new InvalidDataException("Expected ModPlus catalog schema 1 for Revit 2027.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in root.GetProperty("modules").EnumerateArray())
        {
            var name = module.GetProperty("module").GetString();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) throw new InvalidDataException("ModPlus module IDs must be unique and nonempty.");
            if (!module.GetProperty("available2027").GetBoolean()) continue;
            var hash = module.GetProperty("sha256").GetString();
            if (hash?.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException($"Invalid module SHA-256: {name}.");
            SourcePath(Path.Combine(Path.GetTempPath(), "ClaudeRevit-ModPlus-validation"), module.GetProperty("source").GetString() ?? "");
        }
        return root.Clone();
    }

    public static JsonElement Module(JsonElement catalog, string id) =>
        catalog.GetProperty("modules").EnumerateArray().FirstOrDefault(m => m.GetProperty("module").GetString() == id) is { ValueKind: JsonValueKind.Object } found
            ? found : throw new ArgumentException($"Unknown ModPlus module '{id}'. Use get_modplus_tools for exact IDs.");

    public static JsonElement[] Search(JsonElement catalog, string query) => catalog.GetProperty("modules").EnumerateArray()
        .Where(m => query.Length == 0 || new[] { "module", "title", "description", "groups" }
            .Any(key => m.TryGetProperty(key, out var v) && v.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();

    public static string SourcePath(string root, string relative)
    {
        // Portable checks before Path.GetFullPath: reject both Windows and Unix traversal syntax.
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length != 2 || parts[0] != "source" || parts.Any(p => p is "" or "." or ".." || p.Contains(':')) || !parts[1].EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid source path in ModPlus catalog.");
        return Path.GetFullPath(Path.Combine(root, "source", parts[1]));
    }

    public static string Excerpt(string file, int start, int count, int maxChars = 12_000)
    {
        if (start < 1 || count is < 1 or > 300) throw new ArgumentException("Source range: start_line >= 1, line_count 1..300.");
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("Install the local ModPlus analysis bundle to read this source. Catalog metadata is available without it.", file);
        if (info.Length > 20_000_000) throw new InvalidDataException("Source file exceeds 20 MB.");
        var lines = File.ReadLines(file).Skip(start - 1).Take(count).ToArray();
        if (lines.Length == 0) throw new ArgumentException("start_line exceeds the source line count.");
        var text = string.Join("\n", lines.Select((line, i) => $"{start + i}: {line}"));
        return text.Length <= maxChars ? text : text[..maxChars] + "\n… narrow the requested range";
    }

    public static string CommandId(string panel, string button) => $"CustomCtrl_%CustomCtrl_%ModPlus%{panel}%{button}";
    public static bool MatchesCommandId(string id, string panel, string button) => id == CommandId(panel, button);

    // Supersede only the unchanged extension from the supplied archive. Edited overrides keep
    // their normal user-defined-tool behavior and the code-execution switch.
    public static bool IsPreparedExtension(string filename, string sha256) =>
        filename.Equals("ModPlusTools.cs", StringComparison.OrdinalIgnoreCase) &&
        sha256.Equals("f3ce15434c9a20fbecbda059f6800345e0c52d329245fba1b7a5ff101ce64edb", StringComparison.OrdinalIgnoreCase);
}
