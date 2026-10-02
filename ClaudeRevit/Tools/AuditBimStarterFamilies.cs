using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class AuditBimStarterFamilies : IRevitTool
{
    public string Name => "audit_bimstarter_families";
    public string Description => "Read-only local BIMStarter family identity/version audit: RBS_GUID and RBS_VERSION metadata on loaded types, missing/invalid identifiers, duplicate identifiers across families, version differences within each family. Does not contact the cloud or claim a version is current without a server catalog. Inspect nesting separately with analyze_family_structure.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["family_ids"] = NativeToolUtil.Array("integer", "Optional loaded Family IDs; default all."),
        ["limit"] = NativeToolUtil.Field("integer", "Type cap 1..20000, default 5000.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        if (doc.IsFamilyDocument) throw new ToolInputException("Audit loaded library identities in a project; use analyze_family_structure for the open RFA.");
        var ids = input.TryGetValue("family_ids", out var v) ? NativeToolUtil.Ids(v, 10000).ToHashSet() : null;
        var limit = Math.Clamp(ToolInput.OptionalInt(input, "limit") ?? 5000, 1, 20000);
        var types = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(t => ids == null || ids.Contains(t.Family.Id)).Take(limit + 1).ToArray();
        var rows = types.Take(limit).Select(t =>
        {
            ToolContext.ThrowIfCancelled();
            string Value(string name) { var p = t.GetParameters(name); return p.Count == 1 ? p[0].AsString() ?? p[0].AsValueString() ?? "" : ""; }
            var raw = Value("RBS_GUID");
            return new { family_id = t.Family.Id.Value, family = t.Family.Name, type_id = t.Id.Value, type = t.Name, rbs_guid = raw, valid_guid = Guid.TryParse(raw, out var guid) && guid != Guid.Empty, version = Value("RBS_VERSION") };
        }).ToArray();
        var duplicate = rows.Where(r => r.valid_guid).GroupBy(r => Guid.Parse(r.rbs_guid)).Where(g => g.Select(r => r.family_id).Distinct().Count() > 1)
            .Select(g => new { guid = g.Key, family_ids = g.Select(r => r.family_id).Distinct().ToArray() }).ToArray();
        var versionDifferences = rows.GroupBy(r => r.family_id).Where(g => g.Select(r => r.version).Distinct().Count() > 1).Select(g => new { family_id = g.Key, versions = g.Select(r => r.version).Distinct().ToArray() }).ToArray();
        return Services.Json.Serialize(new { types = rows, truncated = types.Length > limit, duplicate_family_ids = duplicate, version_differences = versionDifferences,
            cloud_version_checked = false, note = "Missing RBS metadata does not by itself mean an ordinary non-BIMStarter family is invalid. Duplicates are candidates for review; do not purge automatically." });
    }
}
