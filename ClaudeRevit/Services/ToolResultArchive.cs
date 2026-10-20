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
    public const int MaxContentChars = 2_000_000;
    private const long MaxFileBytes = 20_000_000;
    private const int KeptEntriesOnTrim = 300;

    private static readonly object Gate = new();
    // Hot cache in least-recently-used order (oldest first), with its total size kept alongside.
    // A plain Dictionary cannot serve here: Remove frees a slot, the next insert reuses it, and
    // enumeration follows slots, so "Keys.First()" would evict the entry just added.
    private static readonly Dictionary<string, LinkedListNode<(string Id, string Content)>> Memory = new();
    private static readonly LinkedList<(string Id, string Content)> Recency = new();
    private static long _memoryChars;

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
                Remember(id, content);
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
                if (Memory.TryGetValue(id, out var hit))
                {
                    Recency.Remove(hit);
                    Recency.AddLast(hit);
                    return hit.Value.Content;
                }

                // Cold lookup after a restart: scan the file newest-first.
                // Enumerable.Reverse (not the array's .Reverse()) — overload resolution
                // on a bare string[].Reverse() differs between .NET 8 and .NET 10.
                if (!File.Exists(FilePath)) return null;
                foreach (var line in Enumerable.Reverse(File.ReadAllLines(FilePath)))
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
                            if (content != null) Remember(id, content);
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

    internal static bool IsCached(string id)
    {
        lock (Gate) return Memory.ContainsKey(id);
    }

    private static void Remember(string id, string content)
    {
        if (Memory.Remove(id, out var old))
        {
            Recency.Remove(old);
            _memoryChars -= old.Value.Content.Length;
        }
        Memory[id] = Recency.AddLast((id, content));
        _memoryChars += content.Length;
        // Bound the hot cache as well as the disk archive (UTF-16 character budget).
        while (Recency.Count > 1 && (Recency.Count > KeptEntriesOnTrim || _memoryChars > MaxFileBytes / 2))
        {
            var oldest = Recency.First!;
            Recency.RemoveFirst();
            Memory.Remove(oldest.Value.Id);
            _memoryChars -= oldest.Value.Content.Length;
        }
    }

    private static void TrimIfOversized()
    {
        try
        {
            if (new FileInfo(FilePath).Length <= MaxFileBytes) return;
            var lines = File.ReadAllLines(FilePath);
            var keep = new List<string>(); long bytes = 0;
            foreach (var line in Enumerable.Reverse(lines).Take(KeptEntriesOnTrim))
            {
                var size = System.Text.Encoding.UTF8.GetByteCount(line) + 2;
                if (bytes + size > MaxFileBytes) break;
                keep.Add(line); bytes += size;
            }
            keep.Reverse(); File.WriteAllLines(FilePath, keep);
        }
        catch { /* best-effort */ }
    }
}
