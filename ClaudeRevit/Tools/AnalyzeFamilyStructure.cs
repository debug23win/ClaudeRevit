using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class AnalyzeFamilyStructure : IRevitTool
{
    public string Name => "analyze_family_structure";
    public string Description => "Analyze active RFA or a loaded family (family_id): nested families recursively, shared status, host/placement, nested parameter associations, formulas/dependencies, types, reference planes, labeled dimensions, solid/void forms and import geometry. Background family documents are closed unsaved; reports limits/uneditable children explicitly.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["file_path"] = NativeToolUtil.Field("string", "Optional absolute local RFA path: inspect in a background document, close unsaved; excludes family_id."),
        ["family_id"] = NativeToolUtil.Field("integer", "Optional loaded Family ID in active document; otherwise active RFA."),
        ["max_depth"] = NativeToolUtil.Field("integer", "Nested family depth 0..6, default 3."),
        ["max_families"] = NativeToolUtil.Field("integer", "Total family documents to inspect 1..50, default 20.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        using var scope = new FamilyDocumentScope(app, input);
        var maxDepth = ToolInput.OptionalInt(input, "max_depth") ?? 3;
        var maxFamilies = ToolInput.OptionalInt(input, "max_families") ?? 20;
        if (maxDepth is < 0 or > 6 || maxFamilies is < 1 or > 50) throw new ToolInputException("max_depth 0..6; max_families 1..50.");
        var nodes = new List<object>(); var edges = new List<object>(); var problems = new List<object>(); var count = 0;
        void Analyze(Document doc, string path, int depth, HashSet<string> ancestors)
        {
            ToolContext.ThrowIfCancelled(); count++;
            var fm = FamilyEditorUtil.Manager(doc);
            var allParams = fm.Parameters.Cast<FamilyParameter>().ToList();
            var types = fm.Types.Cast<FamilyType>().ToList();
            var instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
            var forms = new FilteredElementCollector(doc).OfClass(typeof(GenericForm)).Cast<GenericForm>().ToList();
            var parameterRows = allParams.Take(1000).Select(p => new
            {
                name = p.Definition.Name, guid = p.IsShared ? p.GUID.ToString() : null, instance = p.IsInstance,
                spec = p.Definition.GetDataType().TypeId, storage = p.StorageType.ToString(), formula = p.Formula,
                reporting = p.IsReporting, associated_parameter_count = p.AssociatedParameters.Size,
                dependencies = string.IsNullOrEmpty(p.Formula) ? Array.Empty<string>() : allParams.Where(other => other.Id != p.Id &&
                    Regex.IsMatch(p.Formula, @"(?<![\p{L}\p{N}_])" + Regex.Escape(other.Definition.Name) + @"(?![\p{L}\p{N}_])", RegexOptions.None, TimeSpan.FromMilliseconds(100))).Select(other => other.Definition.Name).ToArray()
            }).ToArray();
            var nestedRows = instances.Take(1000).Select(fi => new
            {
                id = fi.Id.Value, family = fi.Symbol.Family.Name, type = fi.Symbol.Name,
                shared = fi.Symbol.Family.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1,
                placement = fi.Symbol.Family.FamilyPlacementType.ToString(), host_id = fi.Host?.Id.Value,
                subcomponent_ids = fi.GetSubComponentIds().Select(id => id.Value).ToArray(),
                associations = fi.Parameters.Cast<Parameter>().Select(p => (Parameter: p, Parent: fm.GetAssociatedFamilyParameter(p))).Where(x => x.Parent != null)
                    .Select(x => new { child_parameter = x.Parameter.Definition.Name, child_guid = x.Parameter.IsShared ? x.Parameter.GUID.ToString() : null,
                        parent_parameter = x.Parent!.Definition.Name, parent_guid = x.Parent.IsShared ? x.Parent.GUID.ToString() : null }).ToArray()
            }).ToArray();
            nodes.Add(new { path, depth, name = doc.OwnerFamily.Name, category = doc.OwnerFamily.FamilyCategory?.Name,
                shared = doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1,
                placement = doc.OwnerFamily.FamilyPlacementType.ToString(), parameters = parameterRows, parameter_count = allParams.Count,
                types = types.Take(200).Select(t => t.Name).ToArray(), type_count = types.Count,
                nested_instances = nestedRows, nested_instance_count = instances.Count,
                forms = forms.Take(500).Select(f => new { id = f.Id.Value, kind = f.GetType().Name, solid = f.IsSolid }).ToArray(), form_count = forms.Count,
                combinations = new FilteredElementCollector(doc).OfClass(typeof(GeomCombination)).Cast<GeomCombination>().Take(500)
                    .Select(c => new { id = c.Id.Value, member_ids = c.AllMembers.Cast<CombinableElement>().Select(e => e.Id.Value).ToArray() }).ToArray(),
                imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).ToElementIds().Select(id => id.Value).ToArray(),
                image_underlays = new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).ToElementIds().Select(id => id.Value).ToArray(),
                reference_planes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Take(500)
                    .Select(p => new { id = p.Id.Value, name = p.Name, normal = NativeToolUtil.Vector(p.Normal) }).ToArray(),
                dimensions = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().Take(1000)
                    .Select(d => new { id = d.Id.Value, label = SafeLabel(d), references_available = SafeReferences(d) }).ToArray(),
                truncated = allParams.Count > 1000 || types.Count > 200 || instances.Count > 1000 || forms.Count > 500 });
            foreach (var family in instances.Select(i => i.Symbol.Family).DistinctBy(f => f.Id.Value))
            {
                ToolContext.ThrowIfCancelled();
                var childPath = path + "/" + family.Name;
                edges.Add(new { parent = path, child = childPath, family_id = family.Id.Value });
                if (depth == maxDepth || count >= maxFamilies) { problems.Add(new { path = childPath, status = "inspection_limit" }); continue; }
                if (!family.IsEditable || family.IsInPlace) { problems.Add(new { path = childPath, status = "uneditable" }); continue; }
                if (ancestors.Contains(family.Name)) { problems.Add(new { path = childPath, status = "repeated_ancestor" }); continue; }
                Document? child = null;
                try
                {
                    child = doc.EditFamily(family);
                    Analyze(child, childPath, depth + 1, new HashSet<string>(ancestors) { family.Name });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { problems.Add(new { path = childPath, status = ex.Message }); }
                finally { if (child is { IsValidObject: true }) child.Close(false); }
            }
        }
        Analyze(scope.Document, scope.Document.OwnerFamily.Name, 0, new() { scope.Document.OwnerFamily.Name });
        return Services.Json.Serialize(new { inspected_families = count, nodes, edges, limitations = problems,
            note = "Element IDs are local to each family document. Analysis does not prove flex/constraint validity; run flex_family." });
    }
    private static string? SafeLabel(Dimension d) { try { return d.FamilyLabel?.Definition.Name; } catch { return null; } }
    private static bool? SafeReferences(Dimension d) { try { return d.AreReferencesAvailable; } catch { return null; } }
}
