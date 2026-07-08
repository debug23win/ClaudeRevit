using System;
using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

// Shared helpers for the Family Editor tools. All of them operate on doc.FamilyManager,
// which only exists while a family document is open for editing — so they fail with one
// clear message when the active document is an ordinary project.
internal static class FamilyEditorUtil
{
    private const double FeetToMm = Units.MmPerFoot;

    public static FamilyManager Manager(Document doc)
    {
        if (!doc.IsFamilyDocument)
            throw new InvalidOperationException(
                "This tool works only in the Family Editor. The active document is a project — " +
                "open or edit a family (.rfa) first.");
        return doc.FamilyManager
            ?? throw new InvalidOperationException("The family document has no FamilyManager.");
    }

    public static FamilyParameter? Find(FamilyManager fm, string name)
    {
        foreach (FamilyParameter p in fm.Parameters)
            if (p.Definition.Name == name) return p;
        return null;
    }

    public static FamilyParameter Require(FamilyManager fm, string name) =>
        Find(fm, name) ?? throw new InvalidOperationException(
            $"Family parameter '{name}' not found. Call get_family_parameters to list the exact names " +
            "(they are case-sensitive and may contain Cyrillic letters).");

    // The parameter "spec" (data type) chosen from a short keyword. These ForgeTypeId
    // constants exist in Revit 2025/2026/2027 alike.
    public static ForgeTypeId SpecFor(string type) => type.Trim().ToLowerInvariant() switch
    {
        "length" => SpecTypeId.Length,
        "number" or "real" => SpecTypeId.Number,
        "integer" or "int" => SpecTypeId.Int.Integer,
        "yesno" or "bool" or "boolean" => SpecTypeId.Boolean.YesNo,
        "text" or "string" => SpecTypeId.String.Text,
        "angle" => SpecTypeId.Angle,
        "area" => SpecTypeId.Area,
        "volume" => SpecTypeId.Volume,
        "force" => SpecTypeId.Force,
        _ => throw new InvalidOperationException(
            $"Unknown parameter type '{type}'. Use one of: length, number, integer, yesno, text, " +
            "angle, area, volume, force.")
    };

    // The UI "group" the parameter is filed under. Geometry (shown as 'Dimensions' in the
    // UI) is the sensible default for the driving/reporting parameters families need most.
    public static ForgeTypeId GroupFor(string? group) => (group ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "geometry" or "dimensions" => GroupTypeId.Geometry,
        "constraints" => GroupTypeId.Constraints,
        "data" or "general" => GroupTypeId.Data,
        "text" => GroupTypeId.Text,
        "materials" => GroupTypeId.Materials,
        "identity" or "identitydata" => GroupTypeId.IdentityData,
        "construction" => GroupTypeId.Construction,
        _ => GroupTypeId.Geometry
    };

    public static bool IsLength(FamilyParameter p)
    {
        try { return p.Definition.GetDataType().TypeId == SpecTypeId.Length.TypeId; }
        catch { return false; }
    }

    public static string SpecId(FamilyParameter p)
    {
        try { return p.Definition.GetDataType().TypeId; } catch { return ""; }
    }

    // The current value of a parameter under the family's active type, in a form worth
    // showing: raw internal value, plus a mm figure for lengths (families are usually
    // authored in mm). Returns null when the family has no types yet.
    public static (object? raw, double? mm, string? display) CurrentValue(FamilyManager fm, FamilyParameter p)
    {
        var ct = fm.CurrentType;
        if (ct == null) return (null, null, null);
        try
        {
            switch (p.StorageType)
            {
                case StorageType.Double:
                    var d = ct.AsDouble(p);
                    if (d == null) return (null, null, null);
                    return (d.Value, IsLength(p) ? d.Value * FeetToMm : (double?)null, SafeValueString(ct, p));
                case StorageType.Integer:
                    return (ct.AsInteger(p), null, SafeValueString(ct, p));
                case StorageType.String:
                    return (ct.AsString(p), null, ct.AsString(p));
                default:
                    return (SafeValueString(ct, p), null, SafeValueString(ct, p));
            }
        }
        catch { return (null, null, null); }
    }

    private static string? SafeValueString(FamilyType ct, FamilyParameter p)
    {
        try { return ct.AsValueString(p); } catch { return null; }
    }

    // A parameter "errors" when its value can't be evaluated under the current type — the
    // classic sign of a broken formula or a bad constraint. This is the health check the
    // family scripts kept running by hand ("foreach param: try AsValueString; count fails").
    public static bool ValueErrors(FamilyManager fm, FamilyParameter p)
    {
        var ct = fm.CurrentType;
        if (ct == null) return false;
        try { ct.AsValueString(p); return false; } catch { return true; }
    }
}
