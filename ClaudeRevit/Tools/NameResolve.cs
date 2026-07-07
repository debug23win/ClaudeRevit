using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

// Resolves an element (usually a type) by name with tolerant matching, and — crucially — a
// helpful failure. Field logs showed the model retrying the SAME wrong name dozens of times
// (a wall type "Б25 200мм" failed 50× in a row) because the error just said "not found". Here
// a miss lists the AVAILABLE names and the nearest match, so the model self-corrects on the
// next call instead of guessing blindly. Tolerant matching (case-insensitive, trimmed) also
// silently fixes the near-misses.
internal static class NameResolve
{
    public static T ByName<T>(Document doc, string? name, string kind,
        Func<FilteredElementCollector, IEnumerable<T>>? source = null) where T : Element
    {
        var col = new FilteredElementCollector(doc).OfClass(typeof(T));
        var all = (source != null ? source(col) : col.Cast<T>()).ToList();

        var hit = all.FirstOrDefault(e => e.Name == name)
                  ?? all.FirstOrDefault(e => string.Equals(e.Name?.Trim(), name?.Trim(),
                         StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;

        throw new InvalidOperationException(NotFound(name, kind, all.Select(e => e.Name)));
    }

    // Builds a "not found" message that names what IS available and the closest match.
    public static string NotFound(string? name, string kind, IEnumerable<string?> available)
    {
        var names = available.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!).Distinct().ToList();

        var msg = $"{kind} '{name}' not found.";
        var nearest = Nearest(name, names);
        if (nearest != null) msg += $" Did you mean '{nearest}'?";

        if (names.Count == 0)
            msg += " (none exist in this document — create or load one first).";
        else
        {
            var shown = names.Count <= 40 ? names : names.Take(40).ToList();
            msg += $" Available {kind.ToLowerInvariant()}s: [{string.Join(", ", shown)}]"
                   + (names.Count > shown.Count ? $" … (+{names.Count - shown.Count} more)" : "")
                   + ". Use one of these exact names — do NOT retry the same missing name.";
        }
        return msg;
    }

    private static string? Nearest(string? name, List<string> names)
    {
        if (string.IsNullOrWhiteSpace(name) || names.Count == 0) return null;

        // A containment match is almost always what the model meant.
        var sub = names.FirstOrDefault(n =>
            n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        if (sub != null) return sub;

        string? best = null;
        var bestDist = int.MaxValue;
        var lname = name.ToLowerInvariant();
        foreach (var n in names)
        {
            var d = Levenshtein(lname, n.ToLowerInvariant());
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return bestDist <= Math.Max(3, name.Length / 2) ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }
}
