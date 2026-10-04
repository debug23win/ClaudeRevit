using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateRebarType : IRevitTool
{
    public string Name => "create_rebar_type";
    public string Description => "Create a native RebarBarType even in an empty architectural project. Set name and diameter_mm; optionally create missing default area/path reinforcement system types. Existing same-name types must match diameter. Does not manufacture a concrete host or certify structural design.";
    public bool RequiresTransaction => true;
    public bool InvalidatesCatalog => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["name"] = NativeToolUtil.Field("string", "Unique type name."),
        ["diameter_mm"] = NativeToolUtil.Field("number", "Nominal/model diameter 1..100 mm."),
        ["create_system_types"] = NativeToolUtil.Field("boolean", "Create missing default area/path types; default true.")
    }, "name", "diameter_mm");
    public void Preflight(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        Services.GeometryPreflight.Name(input["name"].GetString() ?? "");
        var diameter = input["diameter_mm"].GetDouble();
        if (!double.IsFinite(diameter) || diameter is < 1 or > 100) throw new ToolInputException("diameter_mm must be 1..100.");
    }
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        Preflight(input, app);
        var doc = NativeToolUtil.Doc(app); var name = input["name"].GetString()!;
        var diameter = input["diameter_mm"].GetDouble()/Units.MmPerFoot;
        var type = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().FirstOrDefault(t=>t.Name == name);
        var created = type == null;
        if (type != null && Math.Abs(type.BarNominalDiameter-diameter)>1e-8) throw new ToolInputException("Existing type has a different diameter; use another name.");
        if (type == null)
        {
            type=RebarBarType.Create(doc); type.Name=name;
            using var diameters=new BarTypeDiameterOptions
            { BarNominalDiameter=diameter,BarModelDiameter=diameter,StandardBendDiameter=6*diameter,
                StandardHookBendDiameter=6*diameter,StirrupTieBendDiameter=4*diameter };
            type.SetBarTypeDiameters(diameters);
        }
        if (!input.ContainsKey("create_system_types") || ToolInput.Flag(input,"create_system_types"))
        {
            if (!new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType)).Any()) AreaReinforcementType.CreateDefaultAreaReinforcementType(doc);
            if (!new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType)).Any()) PathReinforcementType.CreateDefaultPathReinforcementType(doc);
        }
        return Services.Json.Serialize(new { id=type.Id.Value, name=type.Name, created, diameter_mm=type.BarNominalDiameter*Units.MmPerFoot,
            standard_bend_diameter_mm=type.StandardBendDiameter*Units.MmPerFoot,stirrup_bend_diameter_mm=type.StirrupTieBendDiameter*Units.MmPerFoot,
            warnings=created?new[]{"Initial bend diameters are 6d standard/hooks and 4d stirrups; verify them against the project's reinforcement requirements."}:Array.Empty<string>() });
    }
}
