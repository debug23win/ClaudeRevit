using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Creates a dimension between two references in the Family Editor and optionally labels it
// with a family parameter (so the parameter reports or drives that distance). This is the
// operation that repeatedly ate many execute_csharp calls: building the ReferenceArray from
// FamilyInstance reference types or reference planes, placing the dimension line, applying
// the label, and — crucially — detecting when the dimension fails to persist after regen.
public class CreateFamilyDimension : IRevitTool
{
    public string Name => "create_family_dimension";

    public string Description =>
        "In the Family Editor, creates a dimension between two references and optionally labels it with " +
        "a family parameter. Each anchor is either a nested element (element_id + reference, e.g. " +
        "center_left_right) or a reference plane (reference_plane_id). Labeling with a length family " +
        "parameter makes that parameter report the distance (reporting) or drive it. The dimension line " +
        "is auto-placed between the two anchor positions; pass line_offset_mm to move it off the geometry. " +
        "After creating it the tool regenerates and verifies the dimension persisted (Revit silently " +
        "deletes dimensions whose references can't be held — e.g. array/group members). Works only in " +
        "the Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["anchor1"] = Anchor("First reference."),
            ["anchor2"] = Anchor("Second reference."),
            ["label_parameter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional family parameter name to label the dimension with."
            }),
            ["line_offset_mm"] = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                description = "Optional offset added to both dimension-line endpoints (mm), to place the " +
                              "dimension line clear of the geometry, e.g. {\"z\": -400}.",
                properties = new
                {
                    x = new { type = "number" },
                    y = new { type = "number" },
                    z = new { type = "number" }
                }
            })
        },
        Required = ["anchor1", "anchor2"]
    };

    private static JsonElement Anchor(string desc) => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        description = desc + " Provide EITHER element_id+reference OR reference_plane_id.",
        properties = new
        {
            element_id = new { type = "integer", description = "Nested element id." },
            reference = new
            {
                type = "string",
                @enum = new[]
                {
                    "center_left_right", "left", "right",
                    "center_front_back", "front", "back",
                    "center_elevation", "top", "bottom"
                },
                description = "Which reference of the element (required with element_id)."
            },
            reference_plane_id = new { type = "integer", description = "A reference plane id (alternative to element_id)." }
        }
    });

    public bool RequiresTransaction => true;

    private const double MmToFeet = 1.0 / Units.MmPerFoot;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc); // enforces IsFamilyDocument
        var view = doc.ActiveView
            ?? throw new InvalidOperationException("No active view.");

        var (ref1, pos1) = ResolveAnchor(doc, input["anchor1"], "anchor1");
        var (ref2, pos2) = ResolveAnchor(doc, input["anchor2"], "anchor2");

        var offset = ReadOffset(input);
        var start = pos1.Add(offset);
        var end = pos2.Add(offset);
        if (start.DistanceTo(end) < 1e-6)
            throw new InvalidOperationException(
                "The two anchors project to the same point, so the dimension line would be zero-length. " +
                "Pick references that differ along the measured axis, or add a line_offset_mm.");

        var line = Line.CreateBound(start, end);
        var refArray = new ReferenceArray();
        refArray.Append(ref1);
        refArray.Append(ref2);

        Dimension dim;
        try
        {
            dim = doc.FamilyCreate.NewDimension(view, line, refArray);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Revit could not create the dimension: {ex.Message}. The two references must be parallel " +
                "and visible in the active view; a section/elevation view is usually needed for vertical " +
                "or depth dimensions.");
        }

        string? labelName = null;
        if (input.TryGetValue("label_parameter", out var lp) && lp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(lp.GetString()))
        {
            labelName = lp.GetString();
            var famParam = FamilyEditorUtil.Require(fm, labelName!);
            try { dim.FamilyLabel = famParam; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Created the dimension but could not label it with '{labelName}': {ex.Message}. " +
                    "The label must be a length family parameter (and, to merely report the measured value, " +
                    "an unformula'd one so Revit can make it a reporting parameter).");
            }
        }

        var dimId = dim.Id;
        doc.Regenerate();

        // Revit silently removes dimensions whose references it can't maintain (a very common
        // trap with array/group members) — re-fetch to report the truth instead of a dangling id.
        var refetched = doc.GetElement(dimId) as Dimension;
        var persisted = refetched is { IsValidObject: true };
        double? valueMm = null;
        if (persisted)
            try { valueMm = refetched!.Value.HasValue ? refetched.Value!.Value * Units.MmPerFoot : null; } catch { }

        return JsonSerializer.Serialize(new
        {
            ok = persisted,
            dimension_id = dimId.Value,
            persisted,
            value_mm = valueMm,
            label_parameter = labelName,
            note = persisted
                ? null
                : "The dimension did not survive regeneration — Revit deleted it because it could not " +
                  "hold the chosen references. This usually happens when a reference belongs to an array " +
                  "or group member. Dimension a stable reference (a reference plane, or the array's " +
                  "original member) instead."
        });
    }

    private static XYZ ReadOffset(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("line_offset_mm", out var o) || o.ValueKind != JsonValueKind.Object)
            return XYZ.Zero;
        double Get(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() * MmToFeet : 0;
        return new XYZ(Get("x"), Get("y"), Get("z"));
    }

    // Returns the Reference to dimension and a representative position for placing the line.
    private static (Reference, XYZ) ResolveAnchor(Document doc, JsonElement anchor, string label)
    {
        if (anchor.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{label} must be an object.");

        if (anchor.TryGetProperty("reference_plane_id", out var rpEl) && rpEl.ValueKind == JsonValueKind.Number)
        {
            var rp = doc.GetElement(new ElementId(rpEl.GetInt64())) as ReferencePlane
                ?? throw new InvalidOperationException($"{label}: reference_plane_id is not a reference plane.");
            return (rp.GetReference(), rp.GetPlane().Origin);
        }

        if (!anchor.TryGetProperty("element_id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException(
                $"{label} needs element_id+reference, or reference_plane_id.");

        var id = new ElementId(idEl.GetInt64());
        var fi = doc.GetElement(id) as FamilyInstance
            ?? throw new InvalidOperationException($"{label}: element {id.Value} is not a family instance.");

        var refName = anchor.TryGetProperty("reference", out var rEl) && rEl.ValueKind == JsonValueKind.String
            ? rEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(refName))
            throw new InvalidOperationException($"{label}: 'reference' is required when using element_id.");

        var type = MapReference(refName!);
        var refs = fi.GetReferences(type);
        if (refs == null || refs.Count == 0)
            throw new InvalidOperationException(
                $"{label}: element {id.Value} exposes no '{refName}' reference. Try another reference " +
                "(e.g. center_left_right), or check the nested family actually has that reference plane.");

        var pos = (fi.Location as LocationPoint)?.Point ?? RefPointFallback(doc, id);
        return (refs[0], pos);
    }

    private static XYZ RefPointFallback(Document doc, ElementId id)
    {
        var bb = doc.GetElement(id)?.get_BoundingBox(null);
        return bb != null ? (bb.Min + bb.Max).Multiply(0.5) : XYZ.Zero;
    }

    private static FamilyInstanceReferenceType MapReference(string name) => name.Trim().ToLowerInvariant() switch
    {
        "center_left_right" or "center_lr" => FamilyInstanceReferenceType.CenterLeftRight,
        "left" => FamilyInstanceReferenceType.Left,
        "right" => FamilyInstanceReferenceType.Right,
        "center_front_back" or "center_fb" => FamilyInstanceReferenceType.CenterFrontBack,
        "front" => FamilyInstanceReferenceType.Front,
        "back" => FamilyInstanceReferenceType.Back,
        "center_elevation" or "elevation" => FamilyInstanceReferenceType.CenterElevation,
        "top" => FamilyInstanceReferenceType.Top,
        "bottom" => FamilyInstanceReferenceType.Bottom,
        _ => throw new InvalidOperationException(
            $"Unknown reference '{name}'. Use center_left_right, left, right, center_front_back, " +
            "front, back, center_elevation, top or bottom.")
    };
}
