using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

// Turns a human category name into a BuiltInCategory, tolerantly. The field log showed the
// model repeatedly hitting "Unknown category 'Structural Columns'" / "'Structural Framing'"
// because the old code did a bare Enum.TryParse("OST_" + name): the Revit enum names have no
// spaces (OST_StructuralColumns), so any two-word category the model naturally typed failed.
// Here we also try the space-stripped form and a few common synonyms, and the failure message
// names real categories so the model stops guessing.
internal static class CategoryResolve
{
    public static BuiltInCategory Parse(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new InvalidOperationException("category is required.");

        var raw = category.Trim();
        foreach (var candidate in Candidates(raw))
            if (Enum.TryParse<BuiltInCategory>("OST_" + candidate, ignoreCase: true, out var bic)
                && Enum.IsDefined(typeof(BuiltInCategory), bic))
                return bic;

        throw new InvalidOperationException(
            $"Unknown category '{category}'. Use a Revit category name — spaces are fine — e.g. " +
            "'Walls', 'Floors', 'Roofs', 'Doors', 'Windows', 'Structural Columns', " +
            "'Structural Framing', 'Structural Foundation', 'Furniture', 'Rooms', 'Rebar'. " +
            "Do NOT retry the same unknown name.");
    }

    private static IEnumerable<string> Candidates(string raw)
    {
        yield return raw;                       // "Walls" -> OST_Walls
        yield return raw.Replace(" ", "");      // "Structural Columns" -> OST_StructuralColumns
        if (Synonyms.TryGetValue(raw, out var syn)) yield return syn;
    }

    // Only unambiguous aliases — deliberately NOT mapping bare "Columns" (that is architectural
    // OST_Columns, a real, different category) so we never silently redirect the model.
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Beam"] = "StructuralFraming",
        ["Beams"] = "StructuralFraming",
        ["Framing"] = "StructuralFraming",
        ["Structural Beams"] = "StructuralFraming",
        ["Rebar"] = "Rebar",
        ["Reinforcement"] = "Rebar",
        ["Slab"] = "Floors",
        ["Slabs"] = "Floors",
    };
}
