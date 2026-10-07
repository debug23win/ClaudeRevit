using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Steel;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Steel connection life cycle (КМ/КМД): read connections with approval and code-check status,
// change members/type/approval, solid–solid cuts and fabrication data. Idea from LuDattilo/
// RevitCortex StructuralSteel tools (MIT); native implementations.
public sealed class GetSteelConnections : IRevitTool
{
    public string Name => "get_steel_connections";
    public string Description =>
        "Read steel connections (StructuralConnectionHandler): type, detailed/generic/custom, connected members, " +
        "origin (mm), approval type and code-checking status, plus the project's connection types and approval types. " +
        "Filter by member_ids to see the connections on given members. Read-only.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["member_ids"] = NativeToolUtil.Array("integer", "Only connections touching these members."),
        ["limit"] = NativeToolUtil.Field("integer", "Max connections listed (default 200).")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var filter = input.TryGetValue("member_ids", out var m) && m.ValueKind == JsonValueKind.Array ? NativeToolUtil.Ids(m).ToHashSet() : null;
        var all = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandler)).Cast<StructuralConnectionHandler>()
            .Where(c => filter == null || c.GetConnectedElementIds().Any(filter.Contains)).ToList();
        var limit = input.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number ? Math.Clamp(l.GetInt32(), 1, 2000) : 200;
        return Services.Json.Serialize(new
        {
            count = all.Count,
            by_status = all.GroupBy(c => c.CodeCheckingStatus.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            connections = all.Take(limit).Select(c => new
            {
                id = c.Id.Value, type = doc.GetElement(c.GetTypeId())?.Name, detailed = c.IsDetailed(), custom = c.IsCustom(),
                members = c.GetConnectedElementIds().Select(i => i.Value), origin_mm = Safe(() => NativeToolUtil.Mm(c.GetOrigin())),
                approval = doc.GetElement(c.ApprovalTypeId)?.Name, code_checking = c.CodeCheckingStatus.ToString(), override_type_params = c.OverrideTypeParams
            }),
            connection_types = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandlerType)).Cast<StructuralConnectionHandlerType>()
                .Select(t => new { id = t.Id.Value, name = t.Name, detailed = t.IsDetailed(), generic = t.IsGeneric(), custom = t.IsCustom() }),
            approval_types = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionApprovalType)).Select(t => new { id = t.Id.Value, name = t.Name })
        });
    }
    private static T? Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
}

public sealed class SetSteelConnection : IRevitTool
{
    public string Name => "set_steel_connection";
    public string Description =>
        "Change steel connections: approval type (by name; create_approval_type=true creates it, e.g. 'Согласовано " +
        "КМ'), code-checking status (not_calculated / ok / failed), connection type (by name), add or remove connected " +
        "members, reset the default member order, override type parameters. Applies to connection_ids. preview " +
        "defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["connection_ids"] = NativeToolUtil.Array("integer", "Connections to change."),
        ["approval_type"] = NativeToolUtil.Field("string", "Approval type name."),
        ["create_approval_type"] = NativeToolUtil.Field("boolean", "Create the approval type if missing."),
        ["code_checking_status"] = NativeToolUtil.Field("string", "not_calculated | ok | failed."),
        ["connection_type"] = NativeToolUtil.Field("string", "New connection type name."),
        ["add_member_ids"] = NativeToolUtil.Array("integer", "Members to add."),
        ["remove_member_ids"] = NativeToolUtil.Array("integer", "Members to remove."),
        ["default_order"] = NativeToolUtil.Field("boolean", "Reset the members to the default order."),
        ["override_type_params"] = NativeToolUtil.Field("boolean", "Let instance parameters override the type."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "connection_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var connections = NativeToolUtil.Ids(ToolInput.Required(input, "connection_ids"), 500)
            .Select(id => doc.GetElement(id) as StructuralConnectionHandler ?? throw new ToolInputException($"Element {id.Value} is not a steel connection.")).ToList();
        var approvalName = NativeToolUtil.Text(input, "approval_type");
        var statusText = NativeToolUtil.Text(input, "code_checking_status");
        StructuralConnectionCodeCheckingStatus? status = statusText switch
        {
            "" => null, "not_calculated" => StructuralConnectionCodeCheckingStatus.NotCalculated, "ok" => StructuralConnectionCodeCheckingStatus.OkChecked,
            "failed" => StructuralConnectionCodeCheckingStatus.CheckingFailed, _ => throw new ToolInputException("code_checking_status: not_calculated, ok or failed.")
        };
        var typeName = NativeToolUtil.Text(input, "connection_type");
        StructuralConnectionHandlerType? newType = null;
        if (typeName.Length > 0)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandlerType)).Cast<StructuralConnectionHandlerType>().ToList();
            newType = types.FirstOrDefault(t => t.Name == typeName) ?? throw NameResolve.Missing(typeName, "Connection type", types.Select(t => t.Name));
        }
        var add = input.TryGetValue("add_member_ids", out var ad) && ad.ValueKind == JsonValueKind.Array ? NativeToolUtil.Ids(ad, 20) : null;
        var remove = input.TryGetValue("remove_member_ids", out var rm) && rm.ValueKind == JsonValueKind.Array ? NativeToolUtil.Ids(rm, 20) : null;
        var preview = NativeToolUtil.Preview(input);
        var (changed, warnings) = NativeToolUtil.Commit(doc, "Claude: узлы КМ", preview, () =>
        {
            ElementId? approvalId = null;
            if (approvalName.Length > 0)
            {
                var approvals = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionApprovalType)).ToList();
                approvalId = approvals.FirstOrDefault(a => a.Name == approvalName)?.Id
                    ?? (ToolInput.Flag(input, "create_approval_type") ? StructuralConnectionApprovalType.Create(doc, approvalName).Id
                        : throw NameResolve.Missing(approvalName, "Approval type (or pass create_approval_type=true)", approvals.Select(a => a.Name)));
            }
            var result = new List<object>();
            foreach (var c in connections)
            {
                if (newType != null && c.GetTypeId() != newType.Id) c.ChangeTypeId(newType.Id);
                if (approvalId != null) c.ApprovalTypeId = approvalId;
                if (status != null) c.CodeCheckingStatus = status.Value;
                if (add != null) c.AddElementIds(add);
                if (remove != null) c.RemoveElementIds(remove);
                if (ToolInput.Flag(input, "default_order")) c.SetDefaultElementOrder();
                if (input.TryGetValue("override_type_params", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False) c.OverrideTypeParams = o.GetBoolean();
                result.Add(new { id = c.Id.Value, type = doc.GetElement(c.GetTypeId())?.Name, members = c.GetConnectedElementIds().Select(i => i.Value), approval = doc.GetElement(c.ApprovalTypeId)?.Name, code_checking = c.CodeCheckingStatus.ToString() });
            }
            return result;
        });
        return Services.Json.Serialize(new { preview, connections = changed, revit_warnings = warnings });
    }
}

public sealed class SteelSolidCuts : IRevitTool
{
    public string Name => "steel_solid_cuts";
    public string Description =>
        "Solid–solid cuts between steel or other cuttable elements (coping a beam end by a column, a plate cut by a " +
        "member): action add / remove / list for pairs [{cutting_id, cut_id}]. Before adding, Revit's own check says " +
        "whether the cut is allowed and why not. split_faces splits the cutting solid's faces. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["action"] = NativeToolUtil.Field("string", "add | remove | list."),
        ["pairs"] = NativeToolUtil.Any("add/remove: [{cutting_id, cut_id}]."),
        ["element_ids"] = NativeToolUtil.Array("integer", "list: elements whose cuts to report."),
        ["split_faces"] = NativeToolUtil.Field("boolean", "add: split faces of the cutting solid."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "action");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var action = ToolInput.RequiredString(input, "action");
        if (action == "list")
        {
            var ids = NativeToolUtil.Ids(ToolInput.Required(input, "element_ids"), 500);
            return Services.Json.Serialize(new
            {
                elements = ids.Select(id => NativeToolUtil.Element(doc, id.Value)).Select(e => new
                {
                    id = e.Id.Value, allowed = SolidSolidCutUtils.IsAllowedForSolidCut(e),
                    cutting = SolidSolidCutUtils.GetCuttingSolids(e).Select(i => i.Value), being_cut = SolidSolidCutUtils.GetSolidsBeingCut(e).Select(i => i.Value)
                })
            });
        }
        if (action is not ("add" or "remove")) throw new ToolInputException("action must be add, remove or list.");
        var pairs = ToolInput.RequiredArray(input, "pairs").EnumerateArray().Select(p => (Cutting: NativeToolUtil.Element(doc, p.GetProperty("cutting_id").GetInt64()), Cut: NativeToolUtil.Element(doc, p.GetProperty("cut_id").GetInt64()))).ToList();
        if (pairs.Count is 0 or > 500) throw new ToolInputException("Give 1..500 pairs.");
        var split = ToolInput.Flag(input, "split_faces");
        var preview = NativeToolUtil.Preview(input);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: вырезы КМ", preview, () =>
        {
            var r = new List<object>();
            foreach (var (cutting, cut) in pairs)
            {
                if (action == "remove")
                {
                    var exists = SolidSolidCutUtils.CutExistsBetweenElements(cutting, cut, out _);
                    if (exists) SolidSolidCutUtils.RemoveCutBetweenSolids(doc, cutting, cut);
                    r.Add(new { cutting_id = cutting.Id.Value, cut_id = cut.Id.Value, removed = exists });
                    continue;
                }
                if (!SolidSolidCutUtils.CanElementCutElement(cutting, cut, out var reason))
                { r.Add(new { cutting_id = cutting.Id.Value, cut_id = cut.Id.Value, added = false, reason = reason.ToString() }); continue; }
                SolidSolidCutUtils.AddCutBetweenSolids(doc, cut, cutting, split);
                r.Add(new { cutting_id = cutting.Id.Value, cut_id = cut.Id.Value, added = true });
            }
            return r;
        });
        return Services.Json.Serialize(new { preview, action, results = result, revit_warnings = warnings });
    }
}

public sealed class AddSteelFabricationInfo : IRevitTool
{
    public string Name => "add_steel_fabrication_info";
    public string Description =>
        "Give steel members and plates fabrication identity (SteelElementProperties) so steel connections, " +
        "modifiers and the Advance Steel link can work with them; reports each element's fabrication unique id. " +
        "Elements that already have it are left as they are. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Steel framing, columns or plates."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "element_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var ids = NativeToolUtil.Ids(ToolInput.Required(input, "element_ids"), 5000);
        var missing = ids.Where(id => SteelElementProperties.GetSteelElementProperties(NativeToolUtil.Element(doc, id.Value)) == null).ToList();
        var preview = NativeToolUtil.Preview(input);
        var (added, warnings) = missing.Count == 0 ? (new List<long>(), new List<string>())
            : NativeToolUtil.Commit(doc, "Claude: данные изготовления", preview, () =>
                SteelElementProperties.AddFabricationInformationForRevitElements(doc, missing).Select(i => i.Value).ToList());
        return Services.Json.Serialize(new
        {
            preview, already_had = ids.Count - missing.Count, added = added.Count,
            elements = preview ? null : ids.Select(id => new { id = id.Value, fabrication_uid = SteelElementProperties.GetSteelElementProperties(doc.GetElement(id))?.UniqueID.ToString() }),
            revit_warnings = warnings
        });
    }
}
