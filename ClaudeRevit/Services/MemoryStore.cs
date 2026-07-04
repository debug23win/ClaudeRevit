using System;
using System.IO;
using System.Linq;

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
        AppendTo(FilePath, Load(), note);
    }

    // ---- Per-project notes: keyed by document title AND full path (two different
    // projects are routinely both called Model.rvt — title alone would leak one project's
    // standards into the other). Loaded into the dynamic per-turn context whenever that
    // document is active. For project-specific standards (default cover type, stirrup
    // shapes, preferred view template) that should not pollute the global memory.

    private static string ProjectFilePath(string documentTitle, string documentPath)
    {
        var readable = Sanitize(documentTitle);
        var hash = ShortHash(documentTitle + "|" + documentPath);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeRevit", "projects", readable + "-" + hash + ".md");
    }

    public static string LoadProject(string documentTitle, string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentTitle)) return "";
        try
        {
            var path = ProjectFilePath(documentTitle, documentPath ?? "");
            MigrateLegacyProjectFile(documentTitle, path);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            return "";
        }
    }

    // Notes saved before v1.11 lived at projects/<title>.md (no path hash). Move them to
    // the new name on first access so upgrades don't silently orphan project standards.
    private static void MigrateLegacyProjectFile(string documentTitle, string newPath)
    {
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(newPath)!, Sanitize(documentTitle) + ".md");
            if (File.Exists(legacy) && !File.Exists(newPath))
                File.Move(legacy, newPath);
        }
        catch { /* best-effort */ }
    }

    public static void AppendProject(string documentTitle, string documentPath, string note)
    {
        if (string.IsNullOrWhiteSpace(documentTitle) || string.IsNullOrWhiteSpace(note)) return;
        AppendTo(ProjectFilePath(documentTitle, documentPath ?? ""),
                 LoadProject(documentTitle, documentPath ?? ""), note);
    }

    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static void AppendTo(string path, string existing, string note)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var combined = existing.Length == 0
                ? "- " + note.Trim()
                : existing.TrimEnd() + "\n- " + note.Trim();
            if (combined.Length > MaxChars)
                combined = combined[^MaxChars..];
            File.WriteAllText(path, combined);
        }
        catch { /* best-effort */ }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars);
        return safe.Length > 100 ? safe[..100] : safe;
    }
}
