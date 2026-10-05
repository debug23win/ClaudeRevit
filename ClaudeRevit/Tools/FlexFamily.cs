using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class FlexFamily : IRevitTool
{
    public string Name => "flex_family";
    public string Description => "Test active RFA or loaded family by committing parameter/type changes, regenerating geometry and rolling every scenario back. Default: all existing family types (max 100). Supply scenarios for min/max sizes and yes/no/type variants. Values: length mm, angle degrees, area m2, volume m3, other doubles internal units, material/type as IDs. Reports constraint failures, warnings, geometry and bounds for each tested scenario; never saves changes.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["file_path"] = NativeToolUtil.Field("string", "Optional absolute local RFA path: inspect in a background document, close unsaved; excludes family_id."),
        ["family_id"] = NativeToolUtil.Field("integer", "Optional loaded Family; otherwise active RFA."),
        ["scenarios"] = NativeToolUtil.Any("1..100 objects {name, type_name(optional), values:{parameterNameOrGUID:value}, require_solid(optional bool)}. Default tests all existing types."),
        ["require_geometry_change"] = NativeToolUtil.Field("boolean", "Default false. True rejects scenarios whose values change but solid mesh geometry remains identical. Test each independent size driver separately.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        using var scope = new FamilyDocumentScope(app, input);
        var doc = scope.Document; var fm = doc.FamilyManager;
        var originalType = fm.CurrentType?.Name;
        var types = fm.Types.Cast<FamilyType>().ToDictionary(t => t.Name, StringComparer.Ordinal);
        var scenarios = input.TryGetValue("scenarios", out var supplied) ? supplied.EnumerateArray().Select(s => s.Clone()).ToList()
            : types.Keys.Select(name => JsonSerializer.SerializeToElement(new { name, type_name = name })).ToList();
        if (scenarios.Count is < 1 or > 100) throw new ToolInputException("Supply 1..100 scenarios; empty/type-less families need explicit scenarios.");
        var results = new List<object>();
        var requireChange=ToolInput.Flag(input,"require_geometry_change");
        foreach (var scenario in scenarios)
        {
            ToolContext.ThrowIfCancelled();
            var name = scenario.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "scenario " + (results.Count + 1);
            try
            {
                var (geometry, warnings) = NativeToolUtil.Commit(doc, "Claude: flex " + name, true, () =>
                {
                    if (scenario.TryGetProperty("type_name", out var t))
                        fm.CurrentType = types.TryGetValue(t.GetString() ?? "", out var type) ? type : throw new ToolInputException("Unknown type: " + t);
                    if (fm.CurrentType == null) fm.NewType("Claude flex temporary");
                    doc.Regenerate();
                    if(requireChange&&(!scenario.TryGetProperty("values",out var driverValues)||driverValues.ValueKind!=JsonValueKind.Object||!driverValues.EnumerateObject().Any()))throw new ToolInputException("Geometry-change scenarios need explicit driver values.");
                    // Compare to this scenario's type, so switching to a different type
                    // cannot falsely prove that a non-driving parameter works.
                    var baseline=requireChange?Fingerprint(doc):null;
                    if (scenario.TryGetProperty("values", out var values))
                        foreach (var value in values.EnumerateObject()) FamilyDocumentScope.Set(fm, FamilyDocumentScope.Parameter(fm, value.Name), value.Value);
                    doc.Regenerate();
                    var solids = 0; var volume = 0.0; var geometryObjects = 0;
                    using var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
                    void Probe(GeometryElement? geometry, int depth)
                    {
                        if (geometry == null || depth > 8) return;
                        foreach (var obj in geometry)
                        {
                            ToolContext.ThrowIfCancelled();
                            if (++geometryObjects > 50000) throw new InvalidOperationException("Geometry probe exceeded 50000 objects.");
                            if (obj is Solid s && s.Volume > 1e-10) { solids++; volume += s.Volume; }
                            else if (obj is GeometryInstance i) Probe(i.GetInstanceGeometry(), depth + 1);
                        }
                    }
                    var bounds = new List<object>();
                    foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                    {
                        if (e is not (GenericForm or GeomCombination or FamilyInstance or ImportInstance)) continue;
                        if (e is GenericForm member && member.Combinations.Cast<object>().Any()) continue;
                        Probe(e.get_Geometry(options), 0);
                        var bb = e.get_BoundingBox(null);
                        if (bb != null && bounds.Count < 500)
                        {
                            var corners = (from x in new[] { bb.Min.X, bb.Max.X }
                                           from y in new[] { bb.Min.Y, bb.Max.Y }
                                           from z in new[] { bb.Min.Z, bb.Max.Z }
                                           select bb.Transform.OfPoint(new XYZ(x, y, z))).ToArray();
                            bounds.Add(new { id = e.Id.Value,
                                min_mm = NativeToolUtil.Mm(new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z))),
                                max_mm = NativeToolUtil.Mm(new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z))) });
                        }
                    }
                    var broken = fm.Parameters.Cast<FamilyParameter>().Where(p => FamilyEditorUtil.ValueErrors(fm, p)).Select(p => p.Definition.Name).ToArray();
                    if (broken.Length > 0) throw new InvalidOperationException("Parameter evaluation failed: " + string.Join(", ", broken));
                    if (scenario.TryGetProperty("require_solid", out var rs) && rs.ValueKind == JsonValueKind.True && solids == 0)
                        throw new InvalidOperationException("Scenario requires solid geometry, but none is visible.");
                    var changed=requireChange?Fingerprint(doc)!=baseline:(bool?)null;
                    if(requireChange&&changed!=true)throw new InvalidOperationException("Parameters did not change actual solid geometry; a named parameter alone is not a working driver.");
                    return new { geometry_changed=changed,type_name = fm.CurrentType?.Name, visible_solid_count = solids, summed_solid_volume_m3 = volume * Math.Pow(0.3048, 3), bounds,
                        values = fm.Parameters.Cast<FamilyParameter>().Take(1000).Select(p => new { name = p.Definition.Name, value = FamilyEditorUtil.CurrentValue(fm, p).display }).ToArray() };
                });
                results.Add(new { name, valid = true, geometry, warnings });
            }
            catch (OperationCanceledException) { throw; }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; } // Revit requires abort, do not read a corrupt document.
            catch (Exception ex) { results.Add(new { name, valid = false, error = ex.Message }); }
            if (fm.CurrentType?.Name != originalType) throw new InvalidOperationException("Flex rollback did not restore the original family type; stop and inspect the document.");
        }
        return Services.Json.Serialize(new { family = doc.OwnerFamily.Name, restored = true, results, open_warnings = scope.OpenWarnings,
            coverage = "Only these scenarios were tested. Commit/regen and geometry probes cannot guarantee every size, visibility combination or nested type." });
    }
    private static string Fingerprint(Document doc)
    {
        var values=new List<string>();int vertices=0;
        foreach(var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if(e is not (GenericForm or GeomCombination or FamilyInstance))continue;
            if(e is GenericForm member&&member.Combinations.Cast<object>().Any())continue;
            foreach(var solid in ConnectionNodes.Solids(e))foreach(Face face in solid.Faces)
            {
                var mesh=face.Triangulate();foreach(var p in mesh.Vertices)
                {ToolContext.ThrowIfCancelled();if(++vertices>200000)throw new ToolInputException("Geometry-change check exceeds 200000 mesh vertices; narrow the family.");values.Add(FormattableString.Invariant($"{Math.Round(p.X,7)},{Math.Round(p.Y,7)},{Math.Round(p.Z,7)}"));}
            }
        }
        values.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(";",values))));
    }
}
