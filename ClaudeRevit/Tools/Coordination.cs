using System.IO;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Coordination: clash detection and IFC / Navisworks exchange. Ideas from the rezahanif and
// sky92archangel forks of mcp-servers-for-revit (check_clashes, export_ifc, export_navisworks;
// MIT); native implementations.
public sealed class CheckClashes : IRevitTool
{
    public string Name => "check_clashes";
    public string Description =>
        "Hard-clash detection between two element sets: set_a in this model, set_b in this model or in a linked model " +
        "(set_b.link = link name). Sets are {categories:[...]} and/or {element_ids:[...]}. Pairs are found by bounding box, " +
        "confirmed by solid intersection, and reported with the overlap volume and its centre (mm, host coordinates). " +
        "Joined pairs and host/insert pairs (door in its wall) are ignored by default; min_volume_cm3 drops touching " +
        "contacts. Read-only unless create_view=true (a 3D view boxed to the clashes, set A red, set B yellow; preview " +
        "defaults true).";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["set_a"] = NativeToolUtil.Any("{categories:[...], element_ids:[...]} in this model."),
        ["set_b"] = NativeToolUtil.Any("{categories:[...], element_ids:[...], link:'link name'}; element_ids are link ids when link is set."),
        ["min_volume_cm3"] = NativeToolUtil.Field("number", "Ignore overlaps smaller than this (default 1)."),
        ["include_joined"] = NativeToolUtil.Field("boolean", "Report joined and host/insert pairs too (default false)."),
        ["limit"] = NativeToolUtil.Field("integer", "Max clashes listed (default 300)."),
        ["create_view"] = NativeToolUtil.Field("boolean", "Create a 3D review view of the clashes."),
        ["view_name"] = NativeToolUtil.Field("string", "Review view name (default 'Коллизии <date>')."),
        ["preview"] = NativeToolUtil.Field("boolean", "With create_view: default true.")
    }, "set_a", "set_b");

    private sealed record Hit(Element A, Element B, double? VolumeCm3, XYZ? Center);

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var setA = ToolInput.Required(input, "set_a"); var setB = ToolInput.Required(input, "set_b");
        var linkName = setB.TryGetProperty("link", out var ln) && ln.ValueKind == JsonValueKind.String ? ln.GetString() ?? "" : "";
        Document bDoc = doc; Transform toHost = Transform.Identity;
        if (linkName.Length > 0)
        {
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() != null).ToList();
            var link = links.FirstOrDefault(l => l.Name.Contains(linkName, StringComparison.OrdinalIgnoreCase) || l.GetLinkDocument().Title.Contains(linkName, StringComparison.OrdinalIgnoreCase))
                ?? throw NameResolve.Missing(linkName, "Loaded Revit link", links.Select(l => l.Name));
            bDoc = link.GetLinkDocument(); toHost = link.GetTotalTransform();
        }
        var a = Collect(doc, setA, 5000);
        var bIds = Collect(bDoc, setB, 50000).Select(e => e.Id).ToList();
        if (a.Count == 0 || bIds.Count == 0) throw new ToolInputException($"Set A has {a.Count} and set B {bIds.Count} elements with geometry; both must be non-empty.");
        var minVolume = (input.TryGetValue("min_volume_cm3", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetDouble() : 1) / 28316.846592;   // cm³ → ft³
        var includeJoined = ToolInput.Flag(input, "include_joined");
        var toLink = toHost.Inverse;
        var hits = new List<Hit>(); var seen = new HashSet<(long, long)>(); var failedVolume = 0;
        foreach (var ea in a)
        {
            ToolContext.ThrowIfCancelled();
            var solids = Solids(ea).Select(s => linkName.Length > 0 ? SolidUtils.CreateTransformed(s, toLink) : s).ToList();
            if (solids.Count == 0) continue;
            var box = ea.get_BoundingBox(null); if (box == null) continue;
            var corners = (from x in new[] { box.Min.X, box.Max.X } from y in new[] { box.Min.Y, box.Max.Y } from z in new[] { box.Min.Z, box.Max.Z }
                           select toLink.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z)))).ToList();
            var outline = new Outline(new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)), new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)));
            var near = new FilteredElementCollector(bDoc, bIds).WherePasses(new BoundingBoxIntersectsFilter(outline)).ToElements();
            foreach (var eb in near)
            {
                if (bDoc == doc && (eb.Id == ea.Id || !seen.Add(ea.Id.Value < eb.Id.Value ? (ea.Id.Value, eb.Id.Value) : (eb.Id.Value, ea.Id.Value)))) continue;
                if (!includeJoined && bDoc == doc && (JoinGeometryUtils.AreElementsJoined(doc, ea, eb) || (ea as FamilyInstance)?.Host?.Id == eb.Id || (eb as FamilyInstance)?.Host?.Id == ea.Id)) continue;
                double volume = 0; XYZ? center = null; var intersects = false; var measured = true;
                foreach (var sb in Solids(eb))
                    foreach (var sa in solids)
                    {
                        try
                        {
                            var inter = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                            if (inter != null && inter.Volume > 1e-9) { intersects = true; volume += inter.Volume; center ??= toHost.OfPoint(inter.ComputeCentroid()); }
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                        {
                            // Boolean failed on awkward geometry; fall back to Revit's own intersection test.
                            measured = false;
                            if (new FilteredElementCollector(bDoc, [eb.Id]).WherePasses(new ElementIntersectsSolidFilter(sa)).Any()) intersects = true;
                        }
                    }
                if (!intersects) continue;
                if (!measured) failedVolume++;
                if (measured && volume < minVolume) continue;
                hits.Add(new(ea, eb, measured ? volume * 28316.846592 : null, center));
            }
        }
        var limit = input.TryGetValue("limit", out var lim) && lim.ValueKind == JsonValueKind.Number ? Math.Clamp(lim.GetInt32(), 1, 5000) : 300;
        object Describe(Element e) => new { id = e.Id.Value, category = e.Category?.Name, name = e.Name };
        long? viewId = null; List<string> warnings = new(); bool? preview = null;
        if (ToolInput.Flag(input, "create_view") && hits.Count > 0)
        {
            preview = NativeToolUtil.Preview(input);
            var name = NativeToolUtil.Text(input, "view_name", $"Коллизии {DateTime.Now:yyyy-MM-dd HH-mm}");
            (viewId, warnings) = NativeToolUtil.Commit(doc, "Claude: коллизии", preview.Value, () => (long?)ReviewView(doc, name, hits, bDoc == doc));
        }
        return Services.Json.Serialize(new
        {
            set_a = a.Count, set_b = bIds.Count, link = linkName.Length > 0 ? bDoc.Title : null, clashes = hits.Count,
            by_categories = hits.GroupBy(h => (h.A.Category?.Name, h.B.Category?.Name)).Select(g => new { a = g.Key.Item1, b = g.Key.Item2, count = g.Count() }),
            items = hits.OrderByDescending(h => h.VolumeCm3 ?? double.MaxValue).Take(limit).Select(h => new
            {
                a = Describe(h.A), b = Describe(h.B), volume_cm3 = h.VolumeCm3, center_mm = h.Center == null ? null : NativeToolUtil.Mm(h.Center)
            }),
            truncated = hits.Count > limit, volume_not_measured = failedVolume, preview, view_id = viewId, revit_warnings = warnings
        });
    }

    private static List<Element> Collect(Document doc, JsonElement set, int max)
    {
        if (set.ValueKind != JsonValueKind.Object) throw new ToolInputException("A clash set is an object {categories, element_ids}.");
        var result = new List<Element>();
        if (set.TryGetProperty("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            result.AddRange(NativeToolUtil.Ids(ids, max).Select(id => doc.GetElement(id) ?? throw NameResolve.MissingId(id.Value)));
        if (set.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            var list = cats.EnumerateArray().Select(c => CategoryResolve.Parse(c.GetString())).ToList();
            result.AddRange(new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(list)).WhereElementIsNotElementType()
                .Where(e => e.get_BoundingBox(null) != null));
        }
        if (result.Count == 0 && !set.TryGetProperty("element_ids", out _) && !set.TryGetProperty("categories", out _))
            throw new ToolInputException("A clash set needs categories and/or element_ids.");
        result = result.GroupBy(e => e.Id).Select(g => g.First()).ToList();
        if (result.Count > max) throw new ToolInputException($"A clash set may hold at most {max} elements here ({result.Count}); narrow it by category or level.");
        return result;
    }

    private static List<Solid> Solids(Element e)
    {
        var result = new List<Solid>();
        void Walk(GeometryElement? g, int depth)
        {
            if (g == null || depth > 8) return;
            foreach (var o in g)
                if (o is Solid s && s.Volume > 1e-9) result.Add(s);
                else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry(), depth + 1);
        }
        Walk(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), 0);
        return result;
    }

    private static long ReviewView(Document doc, string name, List<Hit> hits, bool sameDoc)
    {
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
        var view = View3D.CreateIsometric(doc, vft.Id);
        view.Name = name;
        var pts = hits.Where(h => h.Center != null).Select(h => h.Center!).ToList();
        if (pts.Count > 0)
        {
            var pad = 1000 / Units.MmPerFoot;
            view.SetSectionBox(new BoundingBoxXYZ
            {
                Min = new XYZ(pts.Min(p => p.X) - pad, pts.Min(p => p.Y) - pad, pts.Min(p => p.Z) - pad),
                Max = new XYZ(pts.Max(p => p.X) + pad, pts.Max(p => p.Y) + pad, pts.Max(p => p.Z) + pad)
            });
        }
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
        OverrideGraphicSettings Color(byte r, byte g, byte b)
        {
            var o = new OverrideGraphicSettings().SetProjectionLineColor(new Color(r, g, b));
            if (solid != null) o.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(new Color(r, g, b));
            return o;
        }
        foreach (var id in hits.Select(h => h.A.Id).Distinct()) view.SetElementOverrides(id, Color(220, 30, 30));
        if (sameDoc) foreach (var id in hits.Select(h => h.B.Id).Distinct()) view.SetElementOverrides(id, Color(240, 190, 0));
        return view.Id.Value;
    }
}

public sealed class ExportIfc : IRevitTool
{
    public string Name => "export_ifc";
    public string Description =>
        "Export the model to IFC (IFC2x3 default; IFC4, IFC4RV, IFC4DTV, IFC4x3). Optional view_id exports only what that " +
        "view shows. Extra exporter options pass through as {name: value} (e.g. ExportSchedulesAsPsets=true). The export " +
        "runs in a rolled-back transaction, so the model is not changed. Returns the file path.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["output_dir"] = NativeToolUtil.Field("string", "Folder (default Documents)."),
        ["file_name"] = NativeToolUtil.Field("string", "File name without extension (default the document title)."),
        ["version"] = NativeToolUtil.Field("string", "IFC2x3 | IFC4 | IFC4RV | IFC4DTV | IFC4x3 | IFC2x3CV2."),
        ["view_id"] = NativeToolUtil.Field("integer", "Export only elements visible in this view."),
        ["base_quantities"] = NativeToolUtil.Field("boolean", "Export base quantities (default false)."),
        ["space_boundaries"] = NativeToolUtil.Field("integer", "0, 1 or 2 (default 0)."),
        ["options"] = NativeToolUtil.Any("Extra IFC exporter options {name: value}.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        if (!OptionalFunctionalityUtils.IsIFCAvailable()) throw new ToolInputException("IFC export is not available in this Revit installation.");
        var (dir, name) = ExchangeOutput.Path(doc, input, ".ifc");
        var versionText = NativeToolUtil.Text(input, "version", "IFC2x3");
        if (!Enum.TryParse<IFCVersion>(versionText, true, out var version) || version == IFCVersion.Default)
            throw new ToolInputException($"Unknown IFC version '{versionText}'. Use IFC2x3, IFC2x3CV2, IFC4, IFC4RV, IFC4DTV or IFC4x3.");
        using var options = new IFCExportOptions { FileVersion = version, ExportBaseQuantities = ToolInput.Flag(input, "base_quantities") };
        if (input.TryGetValue("space_boundaries", out var sb) && sb.ValueKind == JsonValueKind.Number) options.SpaceBoundaryLevel = Math.Clamp(sb.GetInt32(), 0, 2);
        if (input.TryGetValue("view_id", out var v) && v.ValueKind == JsonValueKind.Number)
            options.FilterViewId = (doc.GetElement(new ElementId(v.GetInt64())) as View ?? throw NameResolve.MissingId(v.GetInt64(), "View")).Id;
        if (input.TryGetValue("options", out var extra) && extra.ValueKind == JsonValueKind.Object)
            foreach (var o in extra.EnumerateObject()) options.AddOption(o.Name, o.Value.ValueKind == JsonValueKind.String ? o.Value.GetString() : o.Value.ToString().ToLowerInvariant());
        // The IFC exporter needs an open transaction; nothing it does should stay in the model.
        var (ok, warnings) = NativeToolUtil.Commit(doc, "Claude: IFC export", preview: true, () => doc.Export(dir, name, options));
        var file = System.IO.Path.Combine(dir, name + ".ifc");
        if (!ok || !File.Exists(file)) throw new InvalidOperationException("Revit reported the IFC export failed." + (warnings.Count > 0 ? " " + string.Join("; ", warnings) : ""));
        return Services.Json.Serialize(new { file, bytes = new FileInfo(file).Length, version = version.ToString(), revit_warnings = warnings });
    }
}

public sealed class ExportNwc : IRevitTool
{
    public string Name => "export_nwc";
    public string Description =>
        "Export to Navisworks NWC (needs the Navisworks exporter installed): the whole model or one 3D view's contents, " +
        "optionally with links, element properties and shared coordinates. Returns the file path.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["output_dir"] = NativeToolUtil.Field("string", "Folder (default Documents)."),
        ["file_name"] = NativeToolUtil.Field("string", "File name without extension (default the document title)."),
        ["view_id"] = NativeToolUtil.Field("integer", "Export only this 3D view's contents (default whole model)."),
        ["export_links"] = NativeToolUtil.Field("boolean", "Include linked models (default false)."),
        ["shared_coordinates"] = NativeToolUtil.Field("boolean", "Use shared coordinates (default true)."),
        ["divide_by_levels"] = NativeToolUtil.Field("boolean", "Divide file into levels (default true).")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            throw new ToolInputException("The Navisworks NWC exporter is not installed for this Revit version (Autodesk 'Navisworks NWC Export Utility').");
        var (dir, name) = ExchangeOutput.Path(doc, input, ".nwc");
        using var options = new NavisworksExportOptions
        {
            ExportScope = NavisworksExportScope.Model,
            ExportLinks = ToolInput.Flag(input, "export_links"),
            ConvertElementProperties = true,
            Coordinates = !input.TryGetValue("shared_coordinates", out var sc) || sc.ValueKind != JsonValueKind.False ? NavisworksCoordinates.Shared : NavisworksCoordinates.Internal,
            DivideFileIntoLevels = !input.TryGetValue("divide_by_levels", out var dl) || dl.ValueKind != JsonValueKind.False
        };
        if (input.TryGetValue("view_id", out var v) && v.ValueKind == JsonValueKind.Number)
        {
            var view = doc.GetElement(new ElementId(v.GetInt64())) as View3D ?? throw new ToolInputException($"Element {v.GetInt64()} is not a 3D view.");
            options.ExportScope = NavisworksExportScope.View; options.ViewId = view.Id;
        }
        doc.Export(dir, name, options);
        var file = System.IO.Path.Combine(dir, name + ".nwc");
        if (!File.Exists(file)) throw new InvalidOperationException("Revit did not write the NWC file.");
        return Services.Json.Serialize(new { file, bytes = new FileInfo(file).Length, scope = options.ExportScope.ToString() });
    }
}

internal static class ExchangeOutput
{
    public static (string Dir, string Name) Path(Document doc, IReadOnlyDictionary<string, JsonElement> input, string extension)
    {
        var dir = NativeToolUtil.Text(input, "output_dir", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Directory.CreateDirectory(dir);
        var name = NativeToolUtil.Text(input, "file_name", string.IsNullOrWhiteSpace(doc.Title) ? "model" : System.IO.Path.GetFileNameWithoutExtension(doc.Title));
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) throw new ToolInputException("file_name contains characters not allowed in a file name.");
        return (dir, name);
    }
}
