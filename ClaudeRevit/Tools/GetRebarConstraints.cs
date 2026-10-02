using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

internal static class RebarConstraintInfo
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string HandleKey(RebarConstrainedHandle h) => Hash(h.GetHandleName() + "|" + h.GetHandleType() + "|" + (h.IsEdgeHandle() ? h.GetEdgeNumber().ToString() : ""));
    public static object Snapshot(Document doc, RebarConstraint? c)
    {
        if (c == null || !c.IsValid()) return new { valid = false };
        string? face = null; string? error = null; Element? target = null; double? distance = null;
        try
        {
            target = c.GetTargetElement();
            if (c.IsToHostFaceOrCover()) face = c.GetTargetHostFaceReference()?.ConvertToStableRepresentation(doc);
            distance = c.IsToCover() ? c.GetDistanceToTargetCover() : c.IsFixedDistanceToHostFace() ? c.GetDistanceToTargetHostFace() :
                c.IsToOtherRebar() ? c.GetDistanceToTargetRebar() : null;
        }
        catch (Exception ex) { error = ex.Message; }
        var kind = c.GetConstraintType().ToString();
        var key = Hash(kind + "|" + target?.UniqueId + "|" + face + "|" + distance?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + error);
        return new { valid = true, candidate_key = key, kind, target_element_id = target?.Id.Value, target_face = face,
            offset_mm = distance * Units.MmPerFoot, editable = error == null && distance != null, error };
    }
    public static string CandidateKey(Document doc, RebarConstraint c) => JsonSerializer.SerializeToElement(Snapshot(doc, c)).GetProperty("candidate_key").GetString()!;
}

public sealed class GetRebarConstraints : IRevitTool
{
    public string Name => "get_rebar_constraints";
    public string Description => "Inspect native rebar constrained handles, current/preferred constraints, cover/face/rebar offsets and actual native candidates for specified target elements. Keys fingerprint the current handle/target/offset; set_rebar_constraint rechecks them. Shape-driven candidate selection is supported; explicit free-form bars generally have no host-face candidates.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["rebar_id"] = NativeToolUtil.Field("integer", "Native Rebar ID."),
        ["target_element_ids"] = NativeToolUtil.Array("integer", "Candidate host/rebar targets, max 20; default current host.")
    }, "rebar_id");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var bar = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "rebar_id")) as Rebar ?? throw new ToolInputException("rebar_id must be native Rebar.");
        var targets = input.TryGetValue("target_element_ids", out var ti) ? NativeToolUtil.Ids(ti, 20) : new List<ElementId> { bar.GetHostId() };
        using var manager = bar.GetRebarConstraintsManager();
        var rows = new List<object>();
        foreach (var handle in manager.GetAllHandles())
        {
            ToolContext.ThrowIfCancelled();
            var candidates = new List<object>();
            foreach (var target in targets)
            {
                try { candidates.Add(new { target_element_id = target.Value, constraints = manager.GetConstraintCandidatesForHandle(handle, target).Take(100).Select(c => RebarConstraintInfo.Snapshot(doc, c)).ToArray() }); }
                catch (Exception ex) { candidates.Add(new { target_element_id = target.Value, error = ex.Message }); }
            }
            object Inspect(Func<RebarConstraint?> get)
            { try { return RebarConstraintInfo.Snapshot(doc, get()); } catch (Exception ex) { return new { valid = false, error = ex.Message }; } }
            rows.Add(new { handle_key = RebarConstraintInfo.HandleKey(handle), name = handle.GetHandleName(), kind = handle.GetHandleType().ToString(),
                current = Inspect(() => manager.GetCurrentConstraintOnHandle(handle)),
                preferred = Inspect(() => manager.GetPreferredConstraintOnHandle(handle)), candidates });
        }
        return Services.Json.Serialize(new { document_key = Services.DocumentSessions.Key(doc), rebar_id = bar.Id.Value,
            shape_driven = bar.IsRebarShapeDriven(), handles = rows });
    }
}

public sealed class SetRebarConstraint : IRevitTool
{
    public string Name => "set_rebar_constraint";
    public string Description => "Select an actual native shape-driven rebar constraint from get_rebar_constraints using document/handle/candidate keys and target_element_id. Refuses stale or ambiguous candidates. Optional signed offset_mm changes cover/host-face/other-rebar distance. Default preview=true commits/regen then rolls back.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["document_key"] = NativeToolUtil.Field("string", "From inspection."),
        ["rebar_id"] = NativeToolUtil.Field("integer", "Native shape-driven Rebar."),
        ["handle_key"] = NativeToolUtil.Field("string", "From inspection."),
        ["candidate_key"] = NativeToolUtil.Field("string", "From inspection."),
        ["target_element_id"] = NativeToolUtil.Field("integer", "Target whose candidate was inspected."),
        ["offset_mm"] = NativeToolUtil.Field("number", "Optional signed native constraint offset."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "document_key", "rebar_id", "handle_key", "candidate_key", "target_element_id");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        if (NativeToolUtil.Text(input, "document_key") != Services.DocumentSessions.Key(doc)) throw new ToolInputException("Constraint inspection belongs to another document.");
        var bar = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "rebar_id")) as Rebar ?? throw new ToolInputException("rebar_id must be Rebar.");
        if (!bar.IsRebarShapeDriven()) throw new ToolInputException("Explicit free-form geometry has no shape-driven preferred constraint workflow.");
        var preview = NativeToolUtil.Preview(input);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: rebar constraint", preview, () =>
        {
            using var manager = bar.GetRebarConstraintsManager();
            var handles = manager.GetAllHandles().Where(h => RebarConstraintInfo.HandleKey(h) == NativeToolUtil.Text(input, "handle_key")).ToArray();
            if (handles.Length != 1) throw new ToolInputException("Handle changed or is ambiguous; inspect again.");
            var handle = handles[0];
            var target = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "target_element_id"));
            var candidates = manager.GetConstraintCandidatesForHandle(handle, target.Id).Where(c => RebarConstraintInfo.CandidateKey(doc, c) == NativeToolUtil.Text(input, "candidate_key")).ToArray();
            if (candidates.Length != 1) throw new ToolInputException("Candidate changed or is ambiguous; inspect again.");
            var constraint = candidates[0];
            if (ToolInput.OptionalDouble(input, "offset_mm") is { } offset)
            {
                if (!double.IsFinite(offset)) throw new ToolInputException("offset_mm must be finite.");
                var feet = offset / Units.MmPerFoot;
                if (constraint.IsToCover()) constraint.SetDistanceToTargetCover(feet);
                else if (constraint.IsFixedDistanceToHostFace()) constraint.SetDistanceToTargetHostFace(feet);
                else if (constraint.IsToOtherRebar()) constraint.SetDistanceToTargetRebar(feet);
                else throw new ToolInputException("This constraint does not support a distance offset.");
            }
            manager.SetPreferredConstraint(constraint);
            doc.Regenerate();
            return RebarConstraintInfo.Snapshot(doc, manager.GetCurrentConstraintOnHandle(handle));
        });
        return Services.Json.Serialize(new { preview, rebar_id = bar.Id.Value, constraint = result, warnings });
    }
}
