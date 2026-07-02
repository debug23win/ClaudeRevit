using System;
using System.IO;

namespace ClaudeRevit.Services;

// Persistent notes Claude writes for itself (preferences, project standards, learned
// facts). Loaded into a cached system block each session, so knowledge carries across
// conversations and Revit restarts — the basis for "self-learning".
public static class MemoryStore
{
    private const int MaxChars = 12_000; // keep the cached block bounded

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "memory.md");

    public static string Load()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        }
        catch
        {
            return "";
        }
    }

    public static void Append(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var existing = Load();
            var combined = existing.Length == 0
                ? "- " + note.Trim()
                : existing.TrimEnd() + "\n- " + note.Trim();
            if (combined.Length > MaxChars)
                combined = combined[^MaxChars..];
            File.WriteAllText(FilePath, combined);
        }
        catch { /* best-effort */ }
    }
}
