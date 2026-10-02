using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateRebarCoupler : IRevitTool
{
    public string Name => "create_rebar_coupler";
    public string Description => "Create a native Revit rebar coupler between specified ends (0 or 1) of two native Rebar objects, or one end for an end cap. Uses native diameter/position/termination validation; reports Revit's error and rolls back if incompatible. Default preview=true.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["coupler_type_id"] = NativeToolUtil.Field("integer", "Loaded coupler type ID."),
        ["first_rebar_id"] = NativeToolUtil.Field("integer", "First native Rebar."),
        ["first_end"] = NativeToolUtil.Field("integer", "0 or 1."),
        ["second_rebar_id"] = NativeToolUtil.Field("integer", "Optional second native Rebar; omit for end cap."),
        ["second_end"] = NativeToolUtil.Field("integer", "0 or 1, required with second bar."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "coupler_type_id", "first_rebar_id", "first_end");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        RebarReinforcementData Data(string idKey, string endKey)
        {
            var bar = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, idKey)) as Rebar ?? throw new ToolInputException(idKey + " must be native Rebar.");
            var end = ToolInput.RequiredInt(input, endKey);
            if (end is not (0 or 1)) throw new ToolInputException(endKey + " must be 0 or 1.");
            return RebarReinforcementData.Create(bar.Id, end) ?? throw new InvalidOperationException("Rebar cannot supply coupler data.");
        }
        var type = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "coupler_type_id"));
        var first = Data("first_rebar_id", "first_end");
        var second = input.ContainsKey("second_rebar_id") ? Data("second_rebar_id", "second_end") : null;
        var preview = NativeToolUtil.Preview(input);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: rebar coupler", preview, () =>
        {
            var coupler = RebarCoupler.Create(doc, type.Id, first, second, out RebarCouplerError error);
            if (coupler == null || (int)error != 0) throw new InvalidOperationException("Native coupler validation: " + error);
            doc.Regenerate();
            return new { coupler_id = preview ? (long?)null : coupler.Id.Value, native_validation = error.ToString() };
        });
        return Services.Json.Serialize(new { preview, result, warnings });
    }
}
