using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Welded mesh reinforcement (FabricSheet / FabricArea). Idea from LuDattilo/RevitCortex
// (FabricReinforcementTools; MIT); native implementation, plus ГОСТ 23279 mesh types by designation.
public sealed class ListFabricTypes : IRevitTool
{
    public string Name => "list_fabric_types";
    public string Description => "List welded mesh (fabric) sheet types with wire diameters, spacings and sheet size (mm), fabric area types and fabric wire types. Read-only.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new());
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        double Wire(ElementId id) => Math.Round(((doc.GetElement(id) as FabricWireType)?.WireDiameter ?? 0) * Units.MmPerFoot, 2);
        return Json.Serialize(new
        {
            sheet_types = new FilteredElementCollector(doc).OfClass(typeof(FabricSheetType)).Cast<FabricSheetType>().Select(t => new
            {
                id = t.Id.Value, name = t.Name, major_wire_mm = Wire(t.MajorDirectionWireType), major_spacing_mm = Math.Round(t.MajorSpacing * Units.MmPerFoot, 1),
                minor_wire_mm = Wire(t.MinorDirectionWireType), minor_spacing_mm = Math.Round(t.MinorSpacing * Units.MmPerFoot, 1),
                width_mm = Math.Round(t.OverallWidth * Units.MmPerFoot), length_mm = Math.Round(t.OverallLength * Units.MmPerFoot)
            }),
            area_types = new FilteredElementCollector(doc).OfClass(typeof(FabricAreaType)).Select(t => new { id = t.Id.Value, name = t.Name }),
            wire_types = new FilteredElementCollector(doc).OfClass(typeof(FabricWireType)).Cast<FabricWireType>().Select(t => new { id = t.Id.Value, name = t.Name, diameter_mm = Math.Round(t.WireDiameter * Units.MmPerFoot, 2) })
        });
    }
}

public sealed class CreateFabricSheetType : IRevitTool
{
    public string Name => "create_fabric_sheet_type";
    public string Description =>
        "Create a welded mesh sheet type from a ГОСТ 23279 designation, e.g. '4С 5Вр1-100/5Вр1-100 230×500 25/25' " +
        "(longitudinal wire Ø-class-spacing / transverse wire Ø-class-spacing, width × length in cm, overhangs in mm), " +
        "or from explicit diameters/spacings/size. Wire types of the needed diameters are reused or created. The type is " +
        "named by the designation unless name is given. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["designation"] = NativeToolUtil.Field("string", "ГОСТ 23279 designation."),
        ["name"] = NativeToolUtil.Field("string", "Type name (default the designation)."),
        ["width_mm"] = NativeToolUtil.Field("number", "Sheet width when the designation has no size."),
        ["length_mm"] = NativeToolUtil.Field("number", "Sheet length when the designation has no size."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "designation");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var text = ToolInput.RequiredString(input, "designation");
        MeshDesignation m;
        try { m = MeshDesignations.Parse(text); } catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        double Size(double? fromDesignation, string key) => fromDesignation ?? (input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw new ToolInputException($"The designation has no sheet size; give {key}."));
        var width = Size(m.WidthMm, "width_mm") / Units.MmPerFoot; var length = Size(m.LengthMm, "length_mm") / Units.MmPerFoot;
        var name = NativeToolUtil.Text(input, "name", text.Trim());
        if (new FilteredElementCollector(doc).OfClass(typeof(FabricSheetType)).Any(t => t.Name == name)) throw new ToolInputException($"A fabric sheet type '{name}' already exists.");
        var preview = NativeToolUtil.Preview(input);
        var (id, warnings) = NativeToolUtil.Commit(doc, "Claude: тип сетки", preview, () =>
        {
            ElementId WireType(double dMm, string cls)
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(FabricWireType)).Cast<FabricWireType>()
                    .FirstOrDefault(w => Math.Abs(w.WireDiameter * Units.MmPerFoot - dMm) < 0.01);
                if (existing != null) return existing.Id;
                var baseType = doc.GetElement(FabricWireType.CreateDefaultFabricWireType(doc)) as FabricWireType;
                var wt = (FabricWireType)baseType!.Duplicate($"{dMm:0.#} {cls}");
                wt.WireDiameter = dMm / Units.MmPerFoot;
                return wt.Id;
            }
            var template = new FilteredElementCollector(doc).OfClass(typeof(FabricSheetType)).Cast<FabricSheetType>().FirstOrDefault()
                ?? (FabricSheetType)doc.GetElement(FabricSheetType.CreateDefaultFabricSheetType(doc));
            var type = (FabricSheetType)template.Duplicate(name);
            type.MajorDirectionWireType = WireType(m.LongDiameterMm, m.LongClass);
            type.MinorDirectionWireType = WireType(m.CrossDiameterMm, m.CrossClass);
            var o1 = (m.Overhang1Mm ?? 25) / Units.MmPerFoot; var o2 = (m.Overhang2Mm ?? 25) / Units.MmPerFoot;
            type.SetMajorLayoutAsActualSpacing(width, o2, m.LongSpacingMm / Units.MmPerFoot);
            type.SetMinorLayoutAsActualSpacing(length, o1, m.CrossSpacingMm / Units.MmPerFoot);
            return type.Id.Value;
        });
        return Json.Serialize(new { preview, type_id = id, name, longitudinal = $"Ø{m.LongDiameterMm} {m.LongClass} шаг {m.LongSpacingMm}", transverse = $"Ø{m.CrossDiameterMm} {m.CrossClass} шаг {m.CrossSpacingMm}", width_mm = width * Units.MmPerFoot, length_mm = length * Units.MmPerFoot, revit_warnings = warnings });
    }
}

public sealed class CreateFabricArea : IRevitTool
{
    public string Name => "create_fabric_area";
    public string Description =>
        "Lay welded mesh sheets over a structural floor, wall or foundation slab (FabricArea): the whole host, or a " +
        "boundary_mm polygon in it; major_direction_deg sets the sheets' major direction in the host plane (floors: from " +
        "the X axis). Sheet type by name (see list_fabric_types / create_fabric_sheet_type). preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["host_id"] = NativeToolUtil.Field("integer", "Structural floor, wall or foundation slab."),
        ["sheet_type"] = NativeToolUtil.Field("string", "Fabric sheet type name."),
        ["area_type"] = NativeToolUtil.Field("string", "Fabric area type name (default the first, created if none)."),
        ["major_direction_deg"] = NativeToolUtil.Field("number", "Major direction angle in the host plane (default 0)."),
        ["boundary_mm"] = NativeToolUtil.Any("Optional closed polygon [[x,y,z], ...] in mm on the host face."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "host_id", "sheet_type");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var host = ReinforcementHelpers.GetValidRebarHost(doc, input);
        var sheetType = FabricTypes.Sheet(doc, ToolInput.RequiredString(input, "sheet_type"));
        var areaName = NativeToolUtil.Text(input, "area_type");
        var angle = (input.TryGetValue("major_direction_deg", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0) * Math.PI / 180;
        var (plane, normal) = FabricTypes.HostFrame(host);
        var major = (plane.XVec * Math.Cos(angle) + plane.YVec * Math.Sin(angle)).Normalize();
        List<XYZ>? boundary = input.TryGetValue("boundary_mm", out var b) && b.ValueKind == JsonValueKind.Array ? b.EnumerateArray().Select(p => NativeToolUtil.Point(p)).ToList() : null;
        if (boundary is { Count: < 3 }) throw new ToolInputException("boundary_mm needs at least three points.");
        var preview = NativeToolUtil.Preview(input);
        var (id, warnings) = NativeToolUtil.Commit(doc, "Claude: сетки по площади", preview, () =>
        {
            var areaTypes = new FilteredElementCollector(doc).OfClass(typeof(FabricAreaType)).ToList();
            var areaTypeId = areaName.Length > 0 ? (areaTypes.FirstOrDefault(t => t.Name == areaName) ?? throw NameResolve.Missing(areaName, "Fabric area type", areaTypes.Select(t => t.Name))).Id
                : areaTypes.FirstOrDefault()?.Id ?? FabricAreaType.CreateDefaultFabricAreaType(doc);
            FabricArea area = boundary == null
                ? FabricArea.Create(doc, host, major, areaTypeId, sheetType.Id)
                : FabricArea.Create(doc, host, [CurveLoop.Create(boundary.Select((p, i) => (Curve)Line.CreateBound(p, boundary[(i + 1) % boundary.Count])).ToList())], major, boundary[0], areaTypeId, sheetType.Id);
            return area.Id.Value;
        });
        return Json.Serialize(new { preview, fabric_area_id = id, host_id = host.Id.Value, sheet_type = sheetType.Name, major_direction = NativeToolUtil.Vector(major), revit_warnings = warnings });
    }
}

public sealed class CreateFabricSheet : IRevitTool
{
    public string Name => "create_fabric_sheet";
    public string Description =>
        "Place one welded mesh sheet in a host: flat at point_mm rotated by rotation_deg in the host plane, or bent " +
        "along bend_profile_mm (an open polyline [[x,y], ...] in mm in the sheet's bending plane — e.g. an L or U for " +
        "slab edges and wall corners). preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["host_id"] = NativeToolUtil.Field("integer", "Concrete host."),
        ["sheet_type"] = NativeToolUtil.Field("string", "Fabric sheet type name."),
        ["point_mm"] = NativeToolUtil.Any("[x,y,z] sheet origin (flat sheet)."),
        ["rotation_deg"] = NativeToolUtil.Field("number", "Rotation in the host plane (flat sheet)."),
        ["bend_profile_mm"] = NativeToolUtil.Any("Bent sheet: open polyline [[x,y], ...] in mm."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "host_id", "sheet_type");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var host = ReinforcementHelpers.GetValidRebarHost(doc, input);
        var type = FabricTypes.Sheet(doc, ToolInput.RequiredString(input, "sheet_type"));
        var preview = NativeToolUtil.Preview(input);
        var bent = input.TryGetValue("bend_profile_mm", out var bp) && bp.ValueKind == JsonValueKind.Array;
        var (id, warnings) = NativeToolUtil.Commit(doc, "Claude: сетка", preview, () =>
        {
            FabricSheet sheet;
            if (bent)
            {
                var pts = bp.EnumerateArray().Select(p => { var c = p.EnumerateArray().Select(v => v.GetDouble()).ToArray(); return new XYZ(c[0] / Units.MmPerFoot, c[1] / Units.MmPerFoot, 0); }).ToList();
                if (pts.Count < 3) throw new ToolInputException("A bend profile needs at least three points (two segments).");
                var profile = CurveLoop.Create(pts.Zip(pts.Skip(1), (p, q) => (Curve)Line.CreateBound(p, q)).ToList());
                sheet = FabricSheet.Create(doc, host.Id, type.Id, profile);
            }
            else sheet = FabricSheet.Create(doc, host, type.Id);
            if (input.TryGetValue("point_mm", out var pt) && pt.ValueKind == JsonValueKind.Array)
            {
                var (plane, normal) = FabricTypes.HostFrame(host);
                var rot = (input.TryGetValue("rotation_deg", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : 0) * Math.PI / 180;
                var x = (plane.XVec * Math.Cos(rot) + plane.YVec * Math.Sin(rot)).Normalize();
                var t = Transform.Identity; t.Origin = NativeToolUtil.Point(pt); t.BasisX = x; t.BasisZ = normal; t.BasisY = normal.CrossProduct(x).Normalize();
                sheet.PlaceInHost(host, t);
            }
            return sheet.Id.Value;
        });
        return Json.Serialize(new { preview, fabric_sheet_id = id, sheet_type = type.Name, bent, revit_warnings = warnings });
    }
}

internal static class FabricTypes
{
    public static FabricSheetType Sheet(Document doc, string name)
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(FabricSheetType)).Cast<FabricSheetType>().ToList();
        return types.FirstOrDefault(t => t.Name == name) ?? throw NameResolve.Missing(name, "Fabric sheet type", types.Select(t => t.Name));
    }

    // The host's main plane: a floor/slab is horizontal; a wall is the plane of its location line.
    public static (Plane Plane, XYZ Normal) HostFrame(Element host)
    {
        if (host is Wall { Location: LocationCurve { Curve: Line l } })
        {
            var along = l.Direction.Normalize();
            var n = along.CrossProduct(XYZ.BasisZ).Normalize();
            return (Plane.CreateByOriginAndBasis(l.GetEndPoint(0), along, XYZ.BasisZ), n);
        }
        return (Plane.CreateByOriginAndBasis(XYZ.Zero, XYZ.BasisX, XYZ.BasisY), XYZ.BasisZ);
    }
}
