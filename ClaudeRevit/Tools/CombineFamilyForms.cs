using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CombineFamilyForms : IRevitTool
{
    public string Name => "combine_family_forms";
    public string Description => "Combine native solids/void forms in the active ordinary Family Editor using Revit CombineElements, e.g. cut a parametric solid with a created void. Default committed preview=true rolls back. Inspect resulting native combination geometry and run flex_family across dimensions/types; combining alone does not prove that the void intersects as intended.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "2..100 native CombinableElement IDs in the family document."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true; false applies.")
    }, "element_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        if (!doc.IsFamilyDocument || doc.OwnerFamily.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_Mass) throw new ToolInputException("Open an ordinary editable family document.");
        var ids = NativeToolUtil.Ids(ToolInput.Required(input, "element_ids"), 100);
        if (ids.Count < 2) throw new ToolInputException("At least two native forms are required.");
        var preview = NativeToolUtil.Preview(input);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: combine family forms", preview, () =>
        {
            using var elements = new CombinableElementArray();
            foreach (var id in ids) elements.Append(NativeToolUtil.Element(doc, id.Value) as CombinableElement ?? throw new ToolInputException("An element is not a native combinable form."));
            var combined = doc.CombineElements(elements); doc.Regenerate();
            using var options = new Options();
            var geometry = combined.get_Geometry(options);
            var solids = geometry?.OfType<Solid>().Where(s => s.Volume > 1e-9).ToArray() ?? [];
            return new { combination_id = preview ? (long?)null : combined.Id.Value, solid_count = solids.Length, total_solid_volume_m3 = solids.Sum(s => s.Volume) * Math.Pow(.3048, 3) };
        });
        return Services.Json.Serialize(new { preview, result, warnings });
    }
}
