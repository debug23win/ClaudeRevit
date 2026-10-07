using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateBeamSystem : IRevitTool
{
    public string Name => "create_beam_system";
    public string Description => "Create a native Revit BeamSystem from a closed planar polygon in millimetres, a structural framing type, level, direction edge and spacing. Beam type must be loaded; generates actual native beams. Preview defaults true and rolls back the committed result.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["boundary_mm"] = NativeToolUtil.Any("3..200 polygon vertices [[x,y,z],...], no repeated last point."),
        ["level_id"] = NativeToolUtil.Field("integer", "Level ID."),
        ["beam_type_id"] = NativeToolUtil.Field("integer", "Structural framing FamilySymbol ID."),
        ["direction_edge"] = NativeToolUtil.Field("integer", "Boundary edge index, default 0."),
        ["spacing_mm"] = NativeToolUtil.Field("number", "Positive beam spacing."),
        ["elevation_mm"] = NativeToolUtil.Field("number", "Offset from level, default 0."),
        ["justification"] = NativeToolUtil.Field("string", "Beginning, End, Center or DirectionLine; default Center."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true; false commits the native system.")
    }, "boundary_mm", "level_id", "beam_type_id", "spacing_mm");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var points = ToolInput.RequiredArray(input, "boundary_mm").EnumerateArray().Select(p => NativeToolUtil.Point(p)).ToList();
        if (points.Count is < 3 or > 200) throw new ToolInputException("Boundary requires 3..200 vertices.");
        var curves = points.Select((p, i) => (Curve)Line.CreateBound(p, points[(i + 1) % points.Count])).ToList();
        var level = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "level_id")) as Level ?? throw new ToolInputException("level_id must be a Level.");
        var symbol = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "beam_type_id")) as FamilySymbol ?? throw new ToolInputException("beam_type_id must be a FamilySymbol.");
        if (symbol.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralFraming) throw new ToolInputException("Select a structural framing type.");
        var edge = ToolInput.OptionalInt(input, "direction_edge") ?? 0;
        if (edge < 0 || edge >= points.Count) throw new ToolInputException("direction_edge is outside boundary.");
        var spacing = ToolInput.RequiredDouble(input, "spacing_mm") / Units.MmPerFoot;
        if (!double.IsFinite(spacing) || spacing <= 0) throw new ToolInputException("spacing_mm must be positive.");
        if (!Enum.TryParse<BeamSystemJustifyType>(NativeToolUtil.Text(input, "justification", "Center"), true, out var justify)) throw new ToolInputException("Unknown beam justification.");
        var preview = NativeToolUtil.Preview(input);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: beam system", preview, () =>
        {
            if (!symbol.IsActive) symbol.Activate();
            var system = BeamSystem.Create(doc, curves, level, edge, false);
            system.BeamType = symbol;
            system.LayoutRule = new LayoutRuleFixedDistance(spacing, justify);
            system.Elevation = (ToolInput.OptionalDouble(input, "elevation_mm") ?? 0) / Units.MmPerFoot;
            doc.Regenerate();
            return new { system_id = preview ? (long?)null : system.Id.Value, beam_ids = preview ? null : system.GetBeamIds().Select(id => id.Value).ToArray(),
                beam_count = system.GetBeamIds().Count, direction = NativeToolUtil.Vector(system.Direction) };
        });
        return Services.Json.Serialize(new { preview, result, warnings });
    }
}
