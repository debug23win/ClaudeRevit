using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Creates a linear array of an element and optionally labels its member count with a family
// parameter (so the count is driven parametrically). Used to array reinforcement hoops in
// the family; also works in a project. Distances are in millimetres.
public class CreateLinearArray : IRevitTool
{
    public string Name => "create_linear_array";

    public string Description =>
        "Arrays an element linearly in the active view: 'count' copies spaced by (spacing_mm) along a " +
        "direction. spacing is the distance between adjacent members, in MILLIMETRES, applied along the " +
        "given direction (default +X). In the Family Editor you can pass label_parameter to bind the " +
        "array's member count to a family parameter so it stays parametric. Returns the new array's id.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_id"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                description = "Id of the element to array (e.g. a rebar/hoop family instance)."
            }),
            ["count"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                minimum = 2,
                description = "Total number of members in the array (including the original)."
            }),
            ["spacing_mm"] = JsonSerializer.SerializeToElement(new
            {
                type = "number",
                description = "Distance between adjacent members, in millimetres."
            }),
            ["direction"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "x", "y", "z", "-x", "-y", "-z" },
                description = "Array direction (default x = east)."
            }),
            ["label_parameter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Family Editor only: name of a family parameter to drive the member count."
            })
        },
        Required = ["element_id", "count", "spacing_mm"]
    };

    public bool RequiresTransaction => true;

    private const double MmToFeet = 1.0 / 304.8;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var view = doc.ActiveView
            ?? throw new InvalidOperationException("No active view.");

        var id = new ElementId(input["element_id"].GetInt64());
        if (doc.GetElement(id) == null)
            throw new InvalidOperationException($"Element {id.Value} not found.");

        var count = input["count"].GetInt32();
        if (count < 2)
            throw new InvalidOperationException("count must be at least 2.");

        var spacingFt = input["spacing_mm"].GetDouble() * MmToFeet;
        var dir = (input.TryGetValue("direction", out var dEl) && dEl.ValueKind == JsonValueKind.String
            ? dEl.GetString() : "x")!.ToLowerInvariant();
        var unit = dir switch
        {
            "y" => new XYZ(0, 1, 0),
            "-y" => new XYZ(0, -1, 0),
            "z" => new XYZ(0, 0, 1),
            "-z" => new XYZ(0, 0, -1),
            "-x" => new XYZ(-1, 0, 0),
            _ => new XYZ(1, 0, 0)
        };
        var translation = unit.Multiply(spacingFt);

        // ArrayAnchorMember.Second makes 'translation' the spacing between adjacent members
        // (rather than the total array length) — matching how the count/spacing parameters
        // are authored.
        var array = LinearArray.Create(doc, view, id, count, translation, ArrayAnchorMember.Second);

        string? labelName = null;
        if (input.TryGetValue("label_parameter", out var lp) && lp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(lp.GetString()))
        {
            if (!doc.IsFamilyDocument)
                throw new InvalidOperationException(
                    "label_parameter is only valid in the Family Editor.");
            labelName = lp.GetString();
            var fm = doc.FamilyManager!;
            var famParam = FamilyEditorUtil.Require(fm, labelName!);
            try { array.Label = famParam; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Created the array (id {array.Id.Value}) but could not label its count with " +
                    $"'{labelName}': {ex.Message}. The label parameter must be an integer family parameter.");
            }
        }
        doc.Regenerate();

        return JsonSerializer.Serialize(new
        {
            array_id = array.Id.Value,
            num_members = array.NumMembers,
            label_parameter = labelName
        });
    }
}
