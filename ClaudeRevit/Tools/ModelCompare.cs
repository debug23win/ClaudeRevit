using System.IO;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Model versions and issue readiness. Ideas from sharafutdinovdi/revit-model-mcp (batch audit
// with snapshots and deltas; MIT) and okuno-dsi/revit-mcp-toolkit (project/element compare;
// Apache-2.0); native implementations, results in the chat rather than spreadsheets.
internal static class SnapshotBuilder
{
    public static Snapshot Build(Document doc, string label, bool withParameters, int maxElements = 200_000)
    {
        var elements = new List<SnapshotElement>();
        var levelNames = new FilteredElementCollector(doc).OfClass(typeof(Level)).ToDictionary(l => l.Id, l => l.Name);
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent())
        {
            if (e.Category is not { CategoryType: CategoryType.Model } cat || e is ElementType) continue;
            if (elements.Count >= maxElements) throw new ToolInputException($"More than {maxElements} model elements; snapshot by category is not supported yet.");
            ToolContext.ThrowIfCancelled();
            var box = e.get_BoundingBox(null);
            var geo = box == null ? "" : ModelSnapshots.Hash(string.Join(",", new[] { box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z }.Select(v => Math.Round(v * Units.MmPerFoot))));
            var p = new Dictionary<string, string>(StringComparer.Ordinal);
            if (withParameters)
                foreach (Parameter prm in e.Parameters)
                {
                    if (!prm.HasValue || prm.Definition == null || prm.StorageType == StorageType.None) continue;
                    var name = prm.Definition.Name;
                    if (p.ContainsKey(name) || name is "Edited by" or "Изменил") continue;
                    var value = prm.StorageType switch
                    {
                        StorageType.String => prm.AsString() ?? "",
                        StorageType.Integer => prm.AsInteger().ToString(),
                        StorageType.Double => Math.Round(prm.AsDouble(), 6).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => prm.AsValueString() ?? prm.AsElementId().Value.ToString()
                    };
                    p[name] = ModelSnapshots.Hash(value);
                }
            var level = e.LevelId != ElementId.InvalidElementId && levelNames.TryGetValue(e.LevelId, out var ln) ? ln : null;
            elements.Add(new(e.UniqueId, e.Id.Value, cat.Name, doc.GetElement(e.GetTypeId())?.Name ?? "", level, geo, p));
        }
        return new(doc.Title, label, DateTime.UtcNow, elements);
    }

    public static string? Resolve(Document doc, string which)
    {
        var folder = ModelSnapshots.Folder(doc.Title);
        if (!Directory.Exists(folder)) return null;
        var files = Directory.GetFiles(folder, "*.json.gz").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        if (which is "" or "latest") return files.FirstOrDefault();
        if (which == "previous") return files.Skip(1).FirstOrDefault();
        return File.Exists(which) ? which : files.FirstOrDefault(f => Path.GetFileName(f).Contains(which, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class SnapshotModel : IRevitTool
{
    public string Name => "snapshot_model";
    public string Description =>
        "Save the current model state as a named snapshot (e.g. the issue 'Изм. 1' or 'Стадия П') for later comparison: " +
        "every model element with category, type, level, a 1 mm geometry key and its parameter values (hashed). Stored " +
        "under %AppData%/ClaudeRevit/snapshots/<document>. list=true lists existing snapshots. Read-only for the model.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["label"] = NativeToolUtil.Field("string", "Snapshot name, e.g. 'Изм.1 выдача 2026-10-07'."),
        ["parameters"] = NativeToolUtil.Field("boolean", "Record parameter values (default true; false is faster)."),
        ["list"] = NativeToolUtil.Field("boolean", "List saved snapshots of this document.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var folder = ModelSnapshots.Folder(doc.Title);
        if (ToolInput.Flag(input, "list"))
            return Services.Json.Serialize(new
            {
                folder, snapshots = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json.gz").OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(f => new { file = Path.GetFileName(f), utc = File.GetLastWriteTimeUtc(f), kb = new FileInfo(f).Length / 1024 }) : null
            });
        var label = NativeToolUtil.Text(input, "label", DateTime.Now.ToString("yyyy-MM-dd HH-mm"));
        var withParams = !input.TryGetValue("parameters", out var wp) || wp.ValueKind != JsonValueKind.False;
        var snapshot = SnapshotBuilder.Build(doc, label, withParams);
        var safe = string.Concat(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {safe}.json.gz");
        ModelSnapshots.Save(snapshot, path);
        return Services.Json.Serialize(new { path, label, elements = snapshot.Elements.Count, by_category = snapshot.Elements.GroupBy(e => e.Category).OrderByDescending(g => g.Count()).Take(20).ToDictionary(g => g.Key, g => g.Count()) });
    }
}

public sealed class CompareModelVersions : IRevitTool
{
    public string Name => "compare_model_versions";
    public string Description =>
        "What changed between two model versions: added, deleted, moved (geometry or level), retyped elements and " +
        "elements whose parameters changed (naming the parameters), by category, with element ids. before/after are " +
        "snapshot names (or 'latest', 'previous', a file path); after defaults to the current model. Read-only. Use " +
        "snapshot_model at each issue; select or color the reported ids to review them.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["before"] = NativeToolUtil.Field("string", "Snapshot to compare from (default 'latest')."),
        ["after"] = NativeToolUtil.Field("string", "Snapshot to compare to (default 'current': the open model now)."),
        ["categories"] = NativeToolUtil.Array("string", "Only these categories (names as in the snapshot)."),
        ["limit"] = NativeToolUtil.Field("integer", "Max changed elements listed (default 200).")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var beforeName = NativeToolUtil.Text(input, "before", "latest");
        var beforePath = SnapshotBuilder.Resolve(doc, beforeName) ?? throw new ToolInputException($"No snapshot '{beforeName}' for this document. Take one with snapshot_model first.");
        var before = ModelSnapshots.Load(beforePath);
        var afterName = NativeToolUtil.Text(input, "after", "current");
        Snapshot after;
        if (afterName == "current") after = SnapshotBuilder.Build(doc, "current", before.Elements.Any(e => e.Params.Count > 0));
        else after = ModelSnapshots.Load(SnapshotBuilder.Resolve(doc, afterName) ?? throw new ToolInputException($"No snapshot '{afterName}' for this document."));
        if (input.TryGetValue("categories", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            var keep = cs.EnumerateArray().Select(c => c.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            before = before with { Elements = before.Elements.Where(e => keep.Contains(e.Category)).ToList() };
            after = after with { Elements = after.Elements.Where(e => keep.Contains(e.Category)).ToList() };
        }
        var diff = ModelSnapshots.Diff(before, after);
        var limit = input.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number ? Math.Clamp(l.GetInt32(), 1, 5000) : 200;
        return Services.Json.Serialize(new
        {
            before = new { file = Path.GetFileName(beforePath), before.Label, before.Utc, elements = before.Elements.Count },
            after = new { label = after.Label, after.Utc, elements = after.Elements.Count },
            totals = new { diff.Added, diff.Deleted, diff.Moved, diff.Retyped, parameters_changed = diff.ParametersChanged },
            by_category = diff.ByCategory,
            most_changed_parameters = diff.Changes.Where(c => c.Kind == "parameters").SelectMany(c => c.Details).GroupBy(n => n).OrderByDescending(g => g.Count()).Take(15).ToDictionary(g => g.Key, g => g.Count()),
            changes = diff.Changes.Take(limit).Select(c => new { element_id = c.ElementId, c.Category, kind = c.Kind, details = c.Details.Take(10) }),
            truncated = diff.Changes.Count > limit,
            note = "Deleted elements keep their old ids (they no longer exist in the model)."
        });
    }
}

public sealed class CheckModelPackage : IRevitTool
{
    public string Name => "check_model_package";
    public string Description =>
        "Pre-issue check of a set of models (комплект): every open project, or the RVT files of a folder (opened " +
        "detached and read-only, closed without saving). Per model: Revit warnings by kind, unplaced / unbounded rooms, " +
        "links not loaded or missing, CAD imports (not links), in-place families, purgeable elements, empty required " +
        "project-information and sheet fields, unsaved changes, and whether the models share the same site location " +
        "and survey point. Read-only; each finding has a severity (error / warning / info).";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["scope"] = NativeToolUtil.Field("string", "open (default: all open projects) | current | folder."),
        ["folder"] = NativeToolUtil.Field("string", "scope=folder: folder with .rvt files (max 30)."),
        ["required_project_parameters"] = NativeToolUtil.Array("string", "Project Information fields that must be filled (e.g. Номер проекта, ADSK_Шифр)."),
        ["required_sheet_parameters"] = NativeToolUtil.Array("string", "Sheet / title block fields that must be filled (e.g. Разработал, Проверил)."),
        ["purge"] = NativeToolUtil.Field("boolean", "Count purgeable elements (slower; default true).")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var scope = NativeToolUtil.Text(input, "scope", "open");
        string[] List(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
        var projectParams = List("required_project_parameters"); var sheetParams = List("required_sheet_parameters");
        var purge = !input.TryGetValue("purge", out var pg) || pg.ValueKind != JsonValueKind.False;
        var reports = new List<Dictionary<string, object?>>(); var locations = new List<(string Doc, string Key)>();
        void Check(Document d, string origin)
        {
            ToolContext.ThrowIfCancelled();
            var findings = new List<PackageFinding>();
            void F(string severity, string what, object? detail = null) => findings.Add(new(severity, what, detail));
            var warnings = d.GetWarnings();
            if (warnings.Count > 0) F(warnings.Count > 200 ? "warning" : "info", $"{warnings.Count} Revit warnings",
                warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Count()} × {g.Key}"));
            var rooms = new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.Architecture.Room>().ToList();
            var unplaced = rooms.Count(r => r.Location == null); var unbounded = rooms.Count(r => r.Location != null && r.Area <= 0);
            if (unplaced > 0) F("warning", $"{unplaced} unplaced rooms");
            if (unbounded > 0) F("error", $"{unbounded} rooms not enclosed or redundant");
            foreach (var lt in new FilteredElementCollector(d).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                var status = lt.GetLinkedFileStatus();
                if (status != LinkedFileStatus.Loaded) F(status == LinkedFileStatus.NotFound ? "error" : "warning", $"link '{lt.Name}' is {status}");
            }
            var imports = new FilteredElementCollector(d).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Count(i => !i.IsLinked);
            if (imports > 0) F("warning", $"{imports} CAD files imported (not linked)");
            var inPlace = new FilteredElementCollector(d).OfClass(typeof(Family)).Cast<Family>().Count(f => f.IsInPlace);
            if (inPlace > 0) F("info", $"{inPlace} in-place families");
            if (purge)
            {
                try { var unused = d.GetUnusedElements(new HashSet<ElementId>()).Count; if (unused > 0) F("info", $"{unused} purgeable elements"); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            foreach (var name in projectParams)
                if (d.ProjectInformation?.LookupParameter(name) is not { } p) F("warning", $"Project Information has no parameter '{name}'");
                else if (string.IsNullOrWhiteSpace(p.AsString() ?? p.AsValueString())) F("error", $"Project Information '{name}' is empty");
            if (sheetParams.Length > 0)
            {
                var sheets = new FilteredElementCollector(d).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
                foreach (var name in sheetParams)
                {
                    var empty = sheets.Where(s =>
                    {
                        var tb = new FilteredElementCollector(d, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
                        var p = s.LookupParameter(name) ?? tb?.LookupParameter(name);
                        return p == null || string.IsNullOrWhiteSpace(p.AsString() ?? p.AsValueString());
                    }).Select(s => s.SheetNumber).ToList();
                    if (empty.Count > 0) F("error", $"'{name}' empty on {empty.Count} of {sheets.Count} sheets", empty.Take(20));
                }
            }
            if (d.IsModified) F("warning", "unsaved changes");
            var site = d.SiteLocation; var survey = BasePoint.GetSurveyPoint(d);
            var key = $"{Math.Round(site.Latitude * 180 / Math.PI, 4)},{Math.Round(site.Longitude * 180 / Math.PI, 4)}|{(survey == null ? "" : string.Join(",", NativeToolUtil.Mm(survey.SharedPosition).Select(v => Math.Round(v))))}";
            locations.Add((d.Title, key));
            reports.Add(new()
            {
                ["model"] = d.Title, ["origin"] = origin, ["path"] = d.PathName, ["workshared"] = d.IsWorkshared,
                ["errors"] = findings.Count(f => f.severity == "error"), ["warnings"] = findings.Count(f => f.severity == "warning"), ["findings"] = findings
            });
        }
        if (scope == "folder")
        {
            var folder = NativeToolUtil.Text(input, "folder");
            if (!Directory.Exists(folder)) throw new ToolInputException($"Folder not found: {folder}");
            var files = Directory.GetFiles(folder, "*.rvt").Where(f => !Path.GetFileName(f).Contains(".0")).Take(31).ToList();
            if (files.Count > 30) throw new ToolInputException("More than 30 RVT files; check a narrower folder.");
            foreach (var file in files)
            {
                Document? opened = null;
                try
                {
                    var info = BasicFileInfo.Extract(file);
                    var options = new OpenOptions { Audit = false, DetachFromCentralOption = info.IsWorkshared ? DetachFromCentralOption.DetachAndPreserveWorksets : DetachFromCentralOption.DoNotDetach };
                    options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                    opened = app.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(file), options);
                    Check(opened, "folder");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { reports.Add(new() { ["model"] = Path.GetFileName(file), ["origin"] = "folder", ["errors"] = 1, ["findings"] = new[] { new PackageFinding("error", "could not open: " + ex.Message, null) } }); }
                finally { try { opened?.Close(false); } catch { } }
            }
        }
        else
        {
            var docs = scope == "current" ? [NativeToolUtil.Doc(app)] : app.Application.Documents.Cast<Document>().Where(d => !d.IsFamilyDocument && !d.IsLinked).ToList();
            foreach (var d in docs) Check(d, "open");
        }
        var distinctLocations = locations.Select(l => l.Key).Distinct().Count();
        return Services.Json.Serialize(new
        {
            models = reports.Count,
            ready = reports.All(r => (int)(r["errors"] ?? 0) == 0) && distinctLocations <= 1,
            shared_location = distinctLocations <= 1 ? "same site location and survey point in every model" : "models differ in site location or survey point",
            location_by_model = distinctLocations <= 1 ? null : locations.Select(l => new { model = l.Doc, location = l.Key }),
            reports
        });
    }
}

internal sealed record PackageFinding(string severity, string what, object? detail);
