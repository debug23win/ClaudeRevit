using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeRevit.Services;

// Disk-backed archive of full tool results that were aged out of the conversation
// (see ToolResultAging) — the reversible half of the compression: the model can pull
// any archived original back via the get_full_result tool, including after a Revit
// restart. Bounded like the script journal so it can't grow without limit.
public static class ToolResultArchive
{
    private const int MaxContentChars = 100_000;
    private const long MaxFileBytes = 20_000_000;
    private const int KeptEntriesOnTrim = 300;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Memory = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "tool_results.jsonl");

    public static void Record(string id, string content)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(content)) return;
        if (content.Length > MaxContentChars)
            content = content[..MaxContentChars] + " …(archive cap)";
        try
        {
            lock (Gate)
            {
                Memory[id] = content;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath,
                    JsonSerializer.Serialize(new { id, content }) + "\n");
                TrimIfOversized();
            }
        }
        catch { /* archiving must never break a turn */ }
    }

    public static string? Lookup(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            lock (Gate)
            {
                if (Memory.TryGetValue(id, out var hit)) return hit;

                // Cold lookup after a restart: scan the file newest-first.
                if (!File.Exists(FilePath)) return null;
                foreach (var line in File.ReadAllLines(FilePath).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("id", out var i) &&
                            i.GetString() == id &&
                            doc.RootElement.TryGetProperty("content", out var c))
                        {
                            var content = c.GetString();
                            if (content != null) Memory[id] = content;
                            return content;
                        }
                    }
                    catch { /* skip torn line */ }
                }
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void TrimIfOversized()
    {
        try
        {
            if (new FileInfo(FilePath).Length <= MaxFileBytes) return;
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, lines.TakeLast(KeptEntriesOnTrim));
        }
        catch { /* best-effort */ }
    }
}
