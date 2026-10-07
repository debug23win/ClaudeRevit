using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// What else an edit would touch, before making it. Idea from bimwright/rvt-mcp
// survey_change_impact (Apache-2.0); native implementation.
public sealed class SurveyChangeImpact : IRevitTool
{
    public string Name => "survey_change_impact";
    public string Description =>
        "Before changing, moving, retyping or deleting elements: what depends on them — hosted inserts (doors, windows, " +
        "openings, rebar), tags and dimensions that reference them, geometry joins, group membership, pinned state, " +
        "workset ownership, and for a type the number of instances using it. Read-only; run it on the elements a bulk " +
        "edit is about to change and mention the consequences to the user.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Elements (or types) about to change."),
        ["limit"] = NativeToolUtil.Field("integer", "Max elements reported in detail (default 50).")
    }, "element_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var ids = NativeToolUtil.Ids(ToolInput.Required(input, "element_ids"), 2000);
        var limit = input.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number ? Math.Clamp(l.GetInt32(), 1, 500) : 50;
        var set = ids.ToHashSet();
        // Dimensions and tags are scanned once for all targets.
        var dimensionRefs = new Dictionary<long, List<long>>();
        foreach (var d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            ReferenceArray? refs = null;
            try { refs = d.References; } catch { }
            if (refs == null) continue;
            foreach (Reference r in refs)
                if (set.Contains(r.ElementId)) (dimensionRefs.TryGetValue(r.ElementId.Value, out var list) ? list : dimensionRefs[r.ElementId.Value] = new()).Add(d.Id.Value);
        }
        var tagRefs = new Dictionary<long, List<long>>();
        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            foreach (var id in t.GetTaggedLocalElementIds().Where(set.Contains))
                (tagRefs.TryGetValue(id.Value, out var list) ? list : tagRefs[id.Value] = new()).Add(t.Id.Value);
        var reports = new List<object>();
        int hosted = 0, joined = 0, grouped = 0, pinned = 0, typeUses = 0, notEditable = 0;
        foreach (var id in ids)
        {
            var e = NativeToolUtil.Element(doc, id.Value);
            var dependents = Safe(() => e.GetDependentElements(null)) ?? [];
            var inserts = dependents.Select(doc.GetElement).Where(x => x != null && x.Id != e.Id && (x is FamilyInstance { Host: not null } fi && fi.Host.Id == e.Id || x is Autodesk.Revit.DB.Structure.Rebar || x is Opening))
                .Select(x => new { id = x!.Id.Value, category = x.Category?.Name }).ToList();
            var joins = e is not ElementType ? Safe(() => JoinGeometryUtils.GetJoinedElements(doc, e)) ?? [] : [];
            var group = e.GroupId != ElementId.InvalidElementId ? doc.GetElement(e.GroupId)?.Name : null;
            var instances = e is ElementType ? new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(x => x.GetTypeId() == e.Id).Count() : 0;
            string? owner = null;
            if (doc.IsWorkshared)
            {
                var status = WorksharingUtils.GetCheckoutStatus(doc, e.Id, out var who);
                if (status == CheckoutStatus.OwnedByOtherUser) { owner = who; notEditable++; }
            }
            hosted += inserts.Count; joined += joins.Count; grouped += group != null ? 1 : 0; pinned += e.Pinned ? 1 : 0; typeUses += instances;
            if (reports.Count < limit)
                reports.Add(new
                {
                    id = id.Value, category = e.Category?.Name, name = e.Name, pinned = e.Pinned, group, owned_by_other_user = owner,
                    hosted_inserts = inserts.Take(30), joined_with = joins.Take(30).Select(j => j.Value),
                    tags = tagRefs.GetValueOrDefault(id.Value)?.Take(30), dimensions = dimensionRefs.GetValueOrDefault(id.Value)?.Take(30),
                    type_instances = e is ElementType ? instances : (int?)null
                });
        }
        return Services.Json.Serialize(new
        {
            elements = ids.Count,
            summary = new
            {
                hosted_inserts = hosted, geometry_joins = joined, in_groups = grouped, pinned, owned_by_other_users = notEditable,
                tags = tagRefs.Values.Sum(v => v.Count), dimensions = dimensionRefs.Values.Sum(v => v.Count), type_instances = typeUses
            },
            details = reports,
            hints = new[]
            {
                hosted > 0 ? "Moving or deleting hosts moves or deletes their inserts and rebar." : null,
                dimensionRefs.Count > 0 ? "Dimensions referencing these elements may lose references or change value." : null,
                grouped > 0 ? "Elements in groups: editing one changes every instance of the group, or needs ungrouping." : null,
                pinned > 0 ? "Pinned elements cannot be moved until unpinned." : null,
                notEditable > 0 ? "Some elements are borrowed by other users; edits to them will fail." : null,
                typeUses > 0 ? "Changing a type changes every instance listed in type_instances." : null
            }.Where(h => h != null)
        });
    }

    private static T? Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
}
