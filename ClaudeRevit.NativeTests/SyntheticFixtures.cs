using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Tools;

namespace ClaudeRevit.NativeTests;

internal static class SyntheticFixtures
{
    // Analytical expectations are computed independently from the generated
    // mesh and from the tool's returned success/flex claims.
    public static object Sections(UIApplication app, JsonElement input)
    {
        var template = input.GetProperty("template").GetString()!;
        var directory = Path.GetFullPath(input.GetProperty("output_directory").GetString()!);
        Directory.CreateDirectory(directory);
        var rows = new List<object>();
        var requested = input.TryGetProperty("shapes", out var shapes) ? shapes.EnumerateArray().Select(s => s.GetString()!).ToArray() : new[] { "rectangle", "box", "i", "channel", "angle", "tube", "timber_pair" };
        foreach (var shape in requested)
        {
            Document? family = null;
            try
            {
                family = app.Application.NewFamilyDocument(template);
                NativeAcceptance.Target(family);
                var n = family.ActiveView?.ViewDirection ?? XYZ.BasisZ;
                if (Math.Abs(n.DotProduct(XYZ.BasisZ)) > .99) n = XYZ.BasisZ;
                double width = 300, height = shape == "tube" ? 300 : 400, web = shape == "timber_pair" ? 40 : 12, flange = 20, length = 1000;
                var args = JsonSerializer.SerializeToElement(new { shape, width_mm = width, height_mm = height, web_mm = web, flange_mm = flange,
                    gap_mm = web, length_mm = length, density_kg_m3 = 7850, preview = false, plane_normal = new[] { n.X, n.Y, n.Z } })
                    .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                var tool = JsonSerializer.Deserialize<JsonElement>(new CreateParametricSection().Execute(args, app));
                var id = new ElementId(tool.GetProperty("result").GetProperty("form_id").GetInt64());
                var form = family.GetElement(id);
                var solids = Solids(form).ToArray();
                double expectedArea = shape switch
                {
                    "rectangle" => width * height,
                    "box" => width * height - (width - 2 * web) * (height - 2 * flange),
                    "i" => 2 * width * flange + web * (height - 2 * flange),
                    "channel" => height * web + 2 * (width - web) * flange,
                    "angle" => height * web + (width - web) * flange,
                    "timber_pair" => (width - web) * height,
                    "tube" => Math.PI * (width * width - (width - 2 * web) * (width - 2 * web)) / 4,
                    _ => throw new InvalidOperationException("Unknown fixture shape.")
                };
                double actualVolumeMm3 = solids.Sum(s => s.Volume) * Math.Pow(304.8, 3), expectedVolumeMm3 = expectedArea * length;
                var curvedTolerance=shape=="tube"?1e-4:1e-5;
                if (Math.Abs(actualVolumeMm3 - expectedVolumeMm3) > Math.Max(1, expectedVolumeMm3 * curvedTolerance)) throw new InvalidOperationException("Independent fixture volume disagrees.");
                if(shape=="tube")
                {
                    var radii=solids.SelectMany(s=>s.Faces.Cast<Face>()).OfType<CylindricalFace>().SelectMany(f=>new[]{f.get_Radius(0).GetLength(),f.get_Radius(1).GetLength()}).Select(r=>Math.Round(r*304.8,5)).Distinct().OrderBy(r=>r).ToArray();
                    if(radii.Length!=2||Math.Abs(radii[0]-138)>.001||Math.Abs(radii[1]-150)>.001)throw new InvalidOperationException("Independent native cylindrical radii disagree.");
                }
                var fm = family.FamilyManager;
                var mass = fm.Parameters.Cast<FamilyParameter>().Single(p => p.Definition.Name == "Section_Mass");
                double actualMass = UnitUtils.ConvertFromInternalUnits(fm.CurrentType.AsDouble(mass)!.Value, UnitTypeId.Kilograms), expectedMass = expectedVolumeMm3 / 1e9 * 7850;
                if (Math.Abs(actualMass - expectedMass) > Math.Max(.001, expectedMass * 1e-5)) throw new InvalidOperationException("Independent fixture mass disagrees.");
                var file = Path.Combine(directory, "QA_Section_" + shape + ".rfa");
                if (File.Exists(file)) throw new InvalidOperationException("Refusing to overwrite an existing fixture: " + file);
                family.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
                NativeAcceptance.Target(null); family.Close(false); family=null;
                rows.Add(new { shape, passed = true, file, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),
                    actual_volume_mm3 = actualVolumeMm3, expected_volume_mm3 = expectedVolumeMm3, actual_mass_kg = actualMass, expected_mass_kg = expectedMass, tool });
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { rows.Add(new { shape, passed = false, error = ex.ToString() }); }
            finally { NativeAcceptance.Target(null); if (family is { IsValidObject: true }) family.Close(false); }
        }
        return rows;
    }
    internal static IEnumerable<Solid> Solids(Element element)
    {
        using var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
        // Detached geometry remains usable after Options/GeometryElement wrappers
        // are released; Revit can otherwise throw on later curved-face traversal.
        return Collect(element.get_Geometry(options)).Select(SolidUtils.Clone).ToArray();
        static IEnumerable<Solid> Collect(GeometryElement? geometry)
        {
            if (geometry == null) yield break;
            foreach (var obj in geometry)
                if (obj is Solid s && s.Volume > 1e-10) yield return s;
                else if (obj is GeometryInstance instance) foreach (var child in Collect(instance.GetInstanceGeometry())) yield return child;
        }
    }
    internal static IReadOnlyDictionary<string, JsonElement> Args(object value) => JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    public static object Checkpoint(UIApplication app, JsonElement input)
    {
        var file = Path.GetFullPath(input.GetProperty("output_file").GetString()!);
        if (File.Exists(file)) throw new InvalidOperationException("Refusing to overwrite fixture.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Document? project = null;
        try
        {
            project = app.Application.NewProjectDocument(UnitSystem.Metric);
            NativeAcceptance.Target(project);
            ToolRegistry.Instance.Register(new CreateLevel());
            var tool = new RunCheckpointJob();
            var plan = new { steps = new[] {
                new { key = "first", tool = "create_level", arguments = new { name = "QA_Checkpoint_A", elevation_ft = 3.5 / .3048 } },
                new { key = "second", tool = "create_level", arguments = new { name = "QA_Checkpoint_B", elevation_ft = 7.0 / .3048 } } } };
            int initial = new FilteredElementCollector(project).OfClass(typeof(Level)).GetElementCount();
            var preview = JsonSerializer.Deserialize<JsonElement>(tool.Execute(Args(new { job_key = "QA_checkpoint", expected_revision = 0, plan, batch_size = 1, preview = true }), app));
            if (new FilteredElementCollector(project).OfClass(typeof(Level)).GetElementCount() != initial) throw new InvalidOperationException("Preview changed native level count.");
            var first = JsonSerializer.Deserialize<JsonElement>(tool.Execute(Args(new { job_key = "QA_checkpoint", expected_revision = 0, plan, batch_size = 1, preview = false }), app));
            var firstId = first.GetProperty("result").GetProperty("steps")[0].GetProperty("result").GetProperty("id").GetInt64();
            if (Math.Abs(((Level)project.GetElement(new ElementId(firstId))).Elevation * .3048 - 3.5) > 1e-7) throw new InvalidOperationException("Checkpoint units are wrong.");
            project.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            NativeAcceptance.Target(null); project.Close(false); project = app.Application.OpenDocumentFile(file); NativeAcceptance.Target(project);
            var persisted = JsonSerializer.Deserialize<JsonElement>(new GetCheckpointJob().Execute(Args(new { job_key = "QA_checkpoint" }), app));
            if (persisted.GetProperty("jobs")[0].GetProperty("completed").GetInt32() != 1) throw new InvalidOperationException("Saved checkpoint lost progress.");
            var second = JsonSerializer.Deserialize<JsonElement>(tool.Execute(Args(new { job_key = "QA_checkpoint", expected_revision = 1, batch_size = 1, preview = false }), app));
            if (!second.GetProperty("result").GetProperty("complete").GetBoolean()) throw new InvalidOperationException("Resume did not complete.");
            var repeat = JsonSerializer.Deserialize<JsonElement>(tool.Execute(Args(new { job_key = "QA_checkpoint", expected_revision = 2, preview = false }), app));
            if (!repeat.GetProperty("no_op").GetBoolean() || new FilteredElementCollector(project).OfClass(typeof(Level)).GetElementCount() != initial + 2) throw new InvalidOperationException("Retry duplicated native elements.");
            using (var tx = new Transaction(project, "QA external checkpoint edit")) { tx.Start(); ((Level)project.GetElement(new ElementId(firstId))).Elevation += 1; tx.Commit(); }
            bool rejected = false;
            try { tool.Execute(Args(new { job_key = "QA_checkpoint", expected_revision = 2, preview = false }), app); }
            catch (ToolInputException ex) when (ex.Message.Contains("externally")) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("External edit did not block resume.");
            return new { passed = true, file, preview_rolled_back = true, reopened_progress = persisted, first, second, repeat, external_edit_rejected = true };
        }
        finally { NativeAcceptance.Target(null); if (project is { IsValidObject: true }) project.Close(false); }
    }
    public static object Nested(UIApplication app, JsonElement input)
    {
        var template = input.GetProperty("template").GetString()!;
        var childPath = Path.GetFullPath(input.GetProperty("child_file").GetString()!);
        var output = Path.GetFullPath(input.GetProperty("output_directory").GetString()!);
        Directory.CreateDirectory(output);
        string middlePath = Path.Combine(output, "QA_Nested_Middle.rfa"), rootPath = Path.Combine(output, "QA_Nested_Root.rfa");
        if (File.Exists(middlePath) || File.Exists(rootPath)) throw new InvalidOperationException("Refusing to overwrite nested fixtures.");
        Document? child = null, middle = null, root = null;
        try
        {
            child = app.Application.OpenDocumentFile(childPath);
            using (var tx = new Transaction(child, "QA shared leaf")) { tx.Start(); child.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED).Set(1); tx.Commit(); }
            middle = app.Application.NewFamilyDocument(template);
            var leaf = child.LoadFamily(middle, new FixtureLoadOptions());
            if (leaf == null) throw new InvalidOperationException("Cannot load shared fixture leaf.");
            using (var tx = new Transaction(middle, "QA constrained nested pair"))
            {
                tx.Start(); var fm = middle.FamilyManager; fm.NewType("QA_Nominal");
                middle.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED).Set(1);
                var width = fm.AddParameter("QA_Width", GroupTypeId.Geometry, SpecTypeId.Length, true); fm.Set(width, 300 / 304.8);
                var visible = fm.AddParameter("QA_SecondVisible", GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true); fm.Set(visible, 1);
                var symbol = (FamilySymbol)middle.GetElement(leaf.GetFamilySymbolIds().First()); symbol.Activate(); middle.Regenerate();
                for (int i = 0; i < 2; i++)
                {
                    var instance = middle.FamilyCreate.NewFamilyInstance(new XYZ(i * 600 / 304.8, 0, 0), symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    fm.AssociateElementParameterToFamilyParameter(instance.LookupParameter("Section_Width"), width);
                    if (i == 1) fm.AssociateElementParameterToFamilyParameter(instance.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), visible);
                }
                tx.Commit();
            }
            middle.SaveAs(middlePath, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            root = app.Application.NewFamilyDocument(template);
            var nested = middle.LoadFamily(root, new FixtureLoadOptions());
            if (nested == null) throw new InvalidOperationException("Cannot load shared middle fixture.");
            using (var tx = new Transaction(root, "QA second nesting level"))
            {
                tx.Start(); var fm = root.FamilyManager; fm.NewType("QA_Nominal");
                var width = fm.AddParameter("QA_Width", GroupTypeId.Geometry, SpecTypeId.Length, true); fm.Set(width, 300 / 304.8);
                var visible = fm.AddParameter("QA_SecondVisible", GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true); fm.Set(visible, 1);
                var symbol = (FamilySymbol)root.GetElement(nested.GetFamilySymbolIds().First()); symbol.Activate(); root.Regenerate();
                var instance = root.FamilyCreate.NewFamilyInstance(XYZ.Zero, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                fm.AssociateElementParameterToFamilyParameter(instance.LookupParameter("QA_Width"), width);
                fm.AssociateElementParameterToFamilyParameter(instance.LookupParameter("QA_SecondVisible"), visible);
                tx.Commit();
            }
            NativeAcceptance.Target(root);
            var structure = JsonSerializer.Deserialize<JsonElement>(new AnalyzeFamilyStructure().Execute(Args(new { max_depth = 4 }), app));
            if (structure.GetProperty("nodes").EnumerateArray().Max(n => n.GetProperty("depth").GetInt32()) < 2) throw new InvalidOperationException("Fixture does not contain two nesting levels.");
            var flex = JsonSerializer.Deserialize<JsonElement>(new FlexFamily().Execute(Args(new { require_geometry_change = true, scenarios = new object[] {
                new { name = "width_240", values = new Dictionary<string,object> { ["QA_Width"] = 240 }, require_solid = true },
                new { name = "width_360", values = new Dictionary<string,object> { ["QA_Width"] = 360 }, require_solid = true },
                new { name = "variant_off", values = new Dictionary<string,object> { ["QA_SecondVisible"] = false }, require_solid = true }
            } }), app));
            if (flex.GetProperty("results").EnumerateArray().Any(r => !r.GetProperty("valid").GetBoolean())) throw new InvalidOperationException("Nested driver/visibility acceptance failed: " + flex);
            // Shared leaves are separate native instances: Revit deliberately omits
            // their solids from the middle instance. Probe only the leaf instances
            // in this fixture, whose middle/root have no owned geometry.
            double nominal = new FilteredElementCollector(root).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.SuperComponent != null && f.GetSubComponentIds().Count==0).SelectMany(Solids).Sum(s => s.Volume) * Math.Pow(.3048, 3);
            if (Math.Abs(nominal - .24) > 1e-6) throw new InvalidOperationException("Two nested 300x400x1000 blocks must occupy 0.24 m3; actual " + nominal + "; instances: " + JsonSerializer.Serialize(new FilteredElementCollector(root).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Select(f=>new { id=f.Id.Value,parent=f.SuperComponent?.Id.Value,volume=Solids(f).Sum(s=>s.Volume)*Math.Pow(.3048,3) })));
            root.SaveAs(rootPath, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            return new { passed = true, file = rootPath, middle_file = middlePath, independent_nominal_volume_m3 = nominal, expected_nominal_volume_m3 = .24, structure, flex };
        }
        finally { NativeAcceptance.Target(null); foreach (var family in new[] { root, middle, child }) if (family is { IsValidObject: true }) family.Close(false); }
    }
    private sealed class FixtureLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues) { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }
    public static object LiveSchedule(UIApplication app, JsonElement input)
    {
        using var oldUpdaters = new InstalledUpdaterPause("ClaudeRevit live SPDS quantities");
        var section = Path.GetFullPath(input.GetProperty("section_file").GetString()!);
        var file = Path.GetFullPath(input.GetProperty("output_file").GetString()!);
        if (File.Exists(file)) throw new InvalidOperationException("Refusing to overwrite fixture.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Document? project = null; IUpdater? updater = null;
        try
        {
            project = app.Application.NewProjectDocument(UnitSystem.Metric); NativeAcceptance.Target(project);
            var updaterType = typeof(CreateSpdsTable).Assembly.GetType("ClaudeRevit.Tools.LiveSpdsUpdater", true)!;
            updater = new ScopedUpdater((IUpdater)Activator.CreateInstance(updaterType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                new object[] { app.ActiveAddInId }, null)!, app.ActiveAddInId, new Guid("d777dc4a-9e10-4699-a055-6b855127fe75"));
            UpdaterRegistry.RegisterUpdater(updater, project, true);
            var filter = new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_GenericModel), new ElementClassFilter(typeof(PropertySetElement)));
            foreach (var change in new[] { Element.GetChangeTypeAny(), Element.GetChangeTypeElementAddition(), Element.GetChangeTypeElementDeletion() })
                UpdaterRegistry.AddTrigger(updater.GetUpdaterId(), project, filter, change);
            FamilyInstance a, b; PropertySetElement assetElement; Material material;
            using (var tx = new Transaction(project, "QA material and native fixture instances"))
            {
                tx.Start();
                if (!project.LoadFamily(section, new FixtureLoadOptions(), out var family)) throw new InvalidOperationException("Cannot load section fixture.");
                var symbol = (FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First()); symbol.Activate(); project.Regenerate();
                var asset = new StructuralAsset("QA actual steel", StructuralAssetClass.Metal) { Density = UnitUtils.ConvertToInternalUnits(7850, UnitTypeId.KilogramsPerCubicMeter) };
                assetElement = PropertySetElement.Create(project, asset);
                material = (Material)project.GetElement(Material.Create(project, "QA actual steel")); material.SetMaterialAspectByPropertySet(MaterialAspect.Structural, assetElement.Id);
                a = project.Create.NewFamilyInstance(XYZ.Zero, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                b = project.Create.NewFamilyInstance(new XYZ(2, 0, 0), symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                a.LookupParameter("Section_Material").Set(material.Id); b.LookupParameter("Section_Material").Set(material.Id);
                tx.Commit();
            }
            var created = JsonSerializer.Deserialize<JsonElement>(new CreateSpdsTable().Execute(Args(new {
                profile = "steel_rollup", standard_edition = "2026", table_key = "QA_live_mass", category = "OST_GenericModel", nesting_policy = "explicit", scope = "entire_category",
                field_mapping = new { profile = new { constant = "QA section" }, grade = new { constant = "S355" }, size = new { constant = "QA nominal" }, group = new { constant = "QA members" }, mass_kg = new { source = "material_volumes" } },
                construction_groups = new[] { "QA members" }, name = "QA live steel mass", font_name = "Arial", preview = false
            }), app));
            var parameters = created.GetProperty("result").GetProperty("derived_parameters");
            var totalGuid = Guid.Parse(parameters.GetProperty("total").GetString()!);
            double Total() => new[] { a, b }.Where(e => e.IsValidObject).Sum(e => UnitUtils.ConvertFromInternalUnits(e.get_Parameter(totalGuid).AsDouble(), UnitTypeId.Kilograms));
            void Expect(double expected, string stage) { var actual = Total(); if (Math.Abs(actual - expected) > .01) throw new InvalidOperationException(stage + ": expected " + expected + " kg; actual " + actual); }
            Expect(1884, "initial actual material quantities");
            using (var tx = new Transaction(project, "QA width edit")) { tx.Start(); a.LookupParameter("Section_Width").Set(500 / 304.8); tx.Commit(); }
            Expect(2512, "width updater");
            using (var tx = new Transaction(project, "QA density edit")) { tx.Start(); var asset = assetElement.GetStructuralAsset(); asset.Name="QA actual steel density 7000"; asset.Density = UnitUtils.ConvertToInternalUnits(7000, UnitTypeId.KilogramsPerCubicMeter); assetElement.SetStructuralAsset(asset); tx.Commit(); }
            Expect(2240, "actual density updater");
            using (var tx = new Transaction(project, "QA source deletion")) { tx.Start(); project.Delete(b.Id); tx.Commit(); }
            Expect(1400, "source deletion updater");
            var nativeSchedules=created.GetProperty("result").GetProperty("schedules").EnumerateArray().ToArray();
            if(nativeSchedules.Length!=5)throw new InvalidOperationException("Steel detail and four native summaries are required.");
            foreach(var entry in nativeSchedules)
            {
                var schedule=(ViewSchedule)project.GetElement(new ElementId(entry.GetProperty("id").GetInt64()));
                var body=schedule.GetTableData().GetSectionData(SectionType.Body);
                var totalRows=Enumerable.Range(body.FirstRowNumber,body.NumberOfRows).Where(row=>double.TryParse(schedule.GetCellText(SectionType.Body,row,body.LastColumnNumber).Replace(',','.'),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)&&Math.Abs(value-1.4)<1e-7).ToArray();
                if(totalRows.Length!=1)throw new InvalidOperationException("Native summary does not show exactly one 1.4 t data row: "+schedule.Name);
                foreach(var row in totalRows)foreach(var col in Enumerable.Range(body.FirstColumnNumber,body.NumberOfColumns))
                {
                    var style=body.GetTableCellStyle(row,col);
                    if(new[]{style.BorderTopLineStyle,style.BorderBottomLineStyle,style.BorderLeftLineStyle,style.BorderRightLineStyle}.Any(id=>id.Value<=0))throw new InvalidOperationException("Native summary data has an invisible/unspecified border.");
                    if(Math.Abs(style.TextSize-2.5*96/25.4)>1e-6)throw new InvalidOperationException("Native summary text size disagrees with 2.5 mm.");
                }
            }
            var audits = created.GetProperty("result").GetProperty("schedules").EnumerateArray().Select(s => JsonSerializer.Deserialize<JsonElement>(new AuditSpdsSchedule().Execute(Args(new { schedule_id = s.GetProperty("id").GetInt64() }), app))).ToArray();
            object? visual=null;
            if(input.TryGetProperty("titleblock_template",out var titleblock)) visual=ScheduleVisual(app,project,created.GetProperty("result").GetProperty("schedules").EnumerateArray().Select(s=>new ElementId(s.GetProperty("id").GetInt64())).ToArray(),titleblock.GetString()!,input.GetProperty("image_directory").GetString()!);
            project.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            return new { passed = true, file, expected_mass_stages_kg = new[] { 1884, 2512, 2240, 1400 }, created, audits, visual, layout_visual_review = "Export pixels still require independent inspection." };
        }
        finally
        {
            NativeAcceptance.Target(null);
            if (updater != null && project is { IsValidObject: true } && UpdaterRegistry.IsUpdaterRegistered(updater.GetUpdaterId(),project)) UpdaterRegistry.UnregisterUpdater(updater.GetUpdaterId(), project);
            if (project is { IsValidObject: true }) project.Close(false);
        }
    }
    private static object ScheduleVisual(UIApplication app,Document project,ElementId[] schedules,string template,string directory)
    {
        Document? block=null;
        try
        {
            Directory.CreateDirectory(directory);
            block=app.Application.NewFamilyDocument(template);
            using(var tx=new Transaction(block,"QA A3 titleblock type")){tx.Start();if(block.FamilyManager.CurrentType==null)block.FamilyManager.NewType("QA A3");tx.Commit();}
            var loaded=block.LoadFamily(project,new FixtureLoadOptions())??throw new InvalidOperationException("Cannot load QA titleblock.");
            var symbol=loaded.GetFamilySymbolIds().First();
            block.Close(false);block=null;
            ViewSheet sheet;
            using(var tx=new Transaction(project,"QA live schedules on A3"))
            {
                tx.Start();sheet=ViewSheet.Create(project,symbol);sheet.Name="QA live specifications";sheet.SheetNumber="QA-01";project.Regenerate();
                var outline=sheet.Outline;var point=new XYZ(outline.Min.U+20/304.8,outline.Max.V-20/304.8,0);
                foreach(var id in schedules)
                {
                    var placed=ScheduleSheetInstance.Create(project,sheet.Id,id,point);project.Regenerate();
                    var box=placed.get_BoundingBox(sheet)??throw new InvalidOperationException("No native schedule bounds.");
                    point=new XYZ(point.X,box.Min.Y-10/304.8,0);
                }
                tx.Commit();
            }
            var audits=schedules.Select(id=>JsonSerializer.Deserialize<JsonElement>(new AuditSpdsSchedule().Execute(Args(new{schedule_id=id.Value}),app))).ToArray();
            var options=new ImageExportOptions{ExportRange=ExportRange.SetOfViews,FilePath=Path.Combine(directory,"QA_A3"),HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG,ZoomType=ZoomFitType.FitToPage,PixelSize=2000,FitDirection=FitDirectionType.Horizontal};
            options.SetViewsAndSheets(new[]{sheet.Id});project.ExportImage(options);
            var sections=schedules.Select(id=>
            {
                var schedule=(ViewSchedule)project.GetElement(id);var data=schedule.GetTableData();var rows=new List<object>();
                foreach(var sectionType in new[]{SectionType.Header,SectionType.Body,SectionType.Summary,SectionType.Footer})
                {
                    var section=data.GetSectionData(sectionType);if(section==null)continue;
                    rows.Add(new{section=sectionType.ToString(),count=section.NumberOfRows,cells=Enumerable.Range(section.FirstRowNumber,section.NumberOfRows).Take(30).Select(row=>new{row,height_mm=section.GetRowHeight(row)*304.8,text=Enumerable.Range(section.FirstColumnNumber,section.NumberOfColumns).Select(col=>schedule.GetCellText(sectionType,row,col)).ToArray(),styles=Enumerable.Range(section.FirstColumnNumber,section.NumberOfColumns).Select(col=>{var style=section.GetTableCellStyle(row,col);return new{style.FontName,style.TextSize,top=style.BorderTopLineStyle.Value,bottom=style.BorderBottomLineStyle.Value,left=style.BorderLeftLineStyle.Value,right=style.BorderRightLineStyle.Value};}).ToArray()}).ToArray()});
                }
                return new{id=id.Value,rows};
            }).ToArray();
            return new{sheet_id=sheet.Id.Value,audits,sections,images=Directory.GetFiles(directory,"*.png"),review="Native pixels must be inspected; this export alone does not certify SPDS compliance."};
        }
        finally{if(block is {IsValidObject:true})block.Close(false);}
    }
    public static object ScheduleForms(UIApplication app,JsonElement input)
    {
        using var installed=new InstalledUpdaterPause("ClaudeRevit live SPDS quantities");
        var project=app.Application.NewProjectDocument(UnitSystem.Metric);NativeAcceptance.Target(project);
        IUpdater? updater=null;
        try
        {
            var type=typeof(CreateSpdsTable).Assembly.GetType("ClaudeRevit.Tools.LiveSpdsUpdater",true)!;
            updater=new ScopedUpdater((IUpdater)Activator.CreateInstance(type,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,null,new object[]{app.ActiveAddInId},null)!,app.ActiveAddInId,Guid.NewGuid());
            UpdaterRegistry.RegisterUpdater(updater,project,true);
            var filter=new ElementCategoryFilter(BuiltInCategory.OST_GenericModel);
            foreach(var change in new[]{Element.GetChangeTypeAny(),Element.GetChangeTypeElementAddition(),Element.GetChangeTypeElementDeletion()})UpdaterRegistry.AddTrigger(updater.GetUpdaterId(),project,filter,change);
            FamilyInstance a,b;
            using(var tx=new Transaction(project,"QA physical timber sources"))
            {
                tx.Start();if(!project.LoadFamily(input.GetProperty("section_file").GetString()!,out var family))throw new InvalidOperationException("Cannot load timber QA fixture.");
                var symbol=(FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First());symbol.Activate();project.Regenerate();
                var asset=new StructuralAsset("QA actual timber density",StructuralAssetClass.Wood){Density=UnitUtils.ConvertToInternalUnits(500,UnitTypeId.KilogramsPerCubicMeter)};
                var physical=PropertySetElement.Create(project,asset);var material=(Material)project.GetElement(Material.Create(project,"QA timber"));material.SetMaterialAspectByPropertySet(MaterialAspect.Structural,physical.Id);
                a=project.Create.NewFamilyInstance(XYZ.Zero,symbol,Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                b=project.Create.NewFamilyInstance(new XYZ(2,0,0),symbol,Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                a.LookupParameter("Section_Material").Set(material.Id);b.LookupParameter("Section_Material").Set(material.Id);tx.Commit();
            }
            var created=new List<JsonElement>();
            foreach(var profile in new[]{"timber_materials","timber_elements","scheme_specification"})
                created.Add(JsonSerializer.Deserialize<JsonElement>(new CreateSpdsTable().Execute(Args(new{profile,standard_edition="2026",table_key="QA_"+profile,category="OST_GenericModel",scope="entire_category",nesting_policy="explicit",name="QA "+profile,font_name="Arial",preview=false,
                    field_mapping=new{name=new{constant="Контрольный деревянный элемент 300x400"},mark=new{constant="ДЭ-1"},designation=new{constant="QA 300x400"},unit=new{constant="м"},notes=new{constant="Плотность 500 кг/м³"},amount=new{parameter_name="Section_Length",unit="m"},mass_kg=new{source="material_volumes"}}
                }),app)));
            double Value(JsonElement table,string key)=>new[]{a,b}.Sum(e=>e.get_Parameter(Guid.Parse(table.GetProperty("result").GetProperty("derived_parameters").GetProperty(key).GetString()!)).AsDouble());
            if(Math.Abs(Value(created[0],"quantity")-2)>1e-7)throw new InvalidOperationException("Initial timber material amount is not 2 m.");
            foreach(var table in created.Skip(1))if(Math.Abs(UnitUtils.ConvertFromInternalUnits(Value(table,"total"),UnitTypeId.Kilograms)-120)>.01)throw new InvalidOperationException("Initial physical timber total is not 120 kg.");
            using(var tx=new Transaction(project,"QA timber length change")){tx.Start();a.LookupParameter("Section_Length").Set(2000/304.8);tx.Commit();}
            if(Math.Abs(Value(created[0],"quantity")-3)>1e-7)throw new InvalidOperationException("Timber material amount did not follow native length.");
            foreach(var table in created.Skip(1))if(Math.Abs(UnitUtils.ConvertFromInternalUnits(Value(table,"total"),UnitTypeId.Kilograms)-180)>.01)throw new InvalidOperationException("Physical timber total did not follow native length.");
            var schedules=created.SelectMany(table=>table.GetProperty("result").GetProperty("schedules").EnumerateArray()).Select(s=>new ElementId(s.GetProperty("id").GetInt64())).ToArray();
            var visual=ScheduleVisual(app,project,schedules,input.GetProperty("titleblock_template").GetString()!,input.GetProperty("image_directory").GetString()!);
            var file=input.GetProperty("output_file").GetString()!;if(File.Exists(file))throw new InvalidOperationException("Refusing to overwrite timber fixture.");Directory.CreateDirectory(Path.GetDirectoryName(file)!);project.SaveAs(file,new SaveAsOptions{MaximumBackups=1});
            return new{passed=true,file,initial_amount_m=2,edited_amount_m=3,initial_mass_kg=120,edited_mass_kg=180,created,visual};
        }
        finally{NativeAcceptance.Target(null);if(updater!=null&&UpdaterRegistry.IsUpdaterRegistered(updater.GetUpdaterId(),project))UpdaterRegistry.UnregisterUpdater(updater.GetUpdaterId(),project);project.Close(false);}
    }
    private sealed class InstalledUpdaterPause : IDisposable
    {
        private readonly List<UpdaterId> _ids = new();
        public InstalledUpdaterPause(string name)
        {
            // A development copy shares storage schema IDs with the installed
            // copy. Pause its older handler only during this one synchronous
            // test callback; no user edits can interleave on the API thread.
            var updaterGuid = name == "ClaudeRevit live SPDS quantities" ? "eac7c404-6d15-45b6-95cf-dc64ad75ce65" : "dc199cba-4d39-4c9d-9858-dab654bd7116";
            var id = new UpdaterId(new AddInId(new Guid("c8a3e9f4-7d2b-4e16-9a5c-3f8b6d4e2a1c")), new Guid(updaterGuid));
            if (UpdaterRegistry.IsUpdaterRegistered(id) && UpdaterRegistry.IsUpdaterEnabled(id)) { _ids.Add(id); UpdaterRegistry.DisableUpdater(id); }
        }
        public void Dispose() { foreach (var id in _ids) UpdaterRegistry.EnableUpdater(id); }
    }
    private sealed class ScopedUpdater(IUpdater implementation, AddInId addin, Guid id) : IUpdater
    {
        public UpdaterId GetUpdaterId()=>new(addin,id);
        public string GetUpdaterName()=>"QA " + implementation.GetUpdaterName();
        public string GetAdditionalInformation()=>implementation.GetAdditionalInformation();
        public ChangePriority GetChangePriority()=>implementation.GetChangePriority();
        public void Execute(UpdaterData data)=>implementation.Execute(data);
    }
    public static object BenchmarkProbe(UIApplication app)
    {
        var project = app.Application.NewProjectDocument(UnitSystem.Metric);
        NativeAcceptance.Target(project);
        try
        {
            string Probe()
            {
                var values = new Dictionary<string, object?> {
                    ["levels"] = new FilteredElementCollector(project).OfClass(typeof(Level)).GetElementCount(),
                    ["materials"] = new FilteredElementCollector(project).OfClass(typeof(Material)).GetElementCount()
                };
                var type = typeof(CreateLevel).Assembly.GetType("ClaudeRevit.Tools.BenchmarkModelProbe", true)!;
                type.GetMethod("Append")!.Invoke(null, new object[] { project, values });
                return JsonSerializer.Serialize(values);
            }
            var before = Probe();
            long id;
            using (var tx = new Transaction(project, "QA benchmark B0"))
            { tx.Start(); var created = JsonSerializer.Deserialize<JsonElement>(new CreateLevel().Execute(Args(new { name = "Bench B0", elevation_ft = 3.5 / .3048 }), app)); id = created.GetProperty("id").GetInt64(); tx.Commit(); }
            var good = ClaudeRevit.Services.BenchmarkObjective.Evaluate("B0", before, Probe());
            if (!good.Passed) throw new InvalidOperationException("Correct native 3.5 m level was not accepted: " + JsonSerializer.Serialize(good));
            using (var tx = new Transaction(project, "QA wrong benchmark height")) { tx.Start(); ((Level)project.GetElement(new ElementId(id))).Elevation = 3.5; tx.Commit(); }
            var bad = ClaudeRevit.Services.BenchmarkObjective.Evaluate("B0", before, Probe());
            if (bad.Passed || bad.Failed == 0) throw new InvalidOperationException("Native unit error was accepted.");
            var materialBefore = Probe();
            using (var tx = new Transaction(project, "QA benchmark B5")) { tx.Start(); new CreateMaterial().Execute(Args(new { name = "Bench Concrete", r = 128, g = 128, b = 128 }), app); tx.Commit(); }
            var material = ClaudeRevit.Services.BenchmarkObjective.Evaluate("B5", materialBefore, Probe());
            if (!material.Passed) throw new InvalidOperationException("Native material evidence failed: " + JsonSerializer.Serialize(material));
            return new { passed = true, correct_level = good, wrong_units_rejected = bad, material_probe = material,
                limitation = "Native probe/objective smoke check only; subscription model execution and crash reproductions require separate runs." };
        }
        finally { NativeAcceptance.Target(null); project.Close(false); }
    }
    public static object DependentNode(UIApplication app, JsonElement input)
    {
        using var oldUpdaters = new InstalledUpdaterPause("ClaudeRevit dependent connection nodes");
        var section = Path.GetFullPath(input.GetProperty("section_file").GetString()!);
        var file = Path.GetFullPath(input.GetProperty("output_file").GetString()!);
        if (File.Exists(file)) throw new InvalidOperationException("Refusing to overwrite fixture.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Document? project = null; IUpdater? updater = null;
        try
        {
            project = app.Application.NewProjectDocument(UnitSystem.Metric); NativeAcceptance.Target(project);
            var updaterType = typeof(UpsertConnectionNode).Assembly.GetType("ClaudeRevit.Tools.ConnectionNodeUpdater", true)!;
            updater = new ScopedUpdater((IUpdater)Activator.CreateInstance(updaterType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                new object[] { app.ActiveAddInId }, null)!, app.ActiveAddInId, new Guid("dd181d42-0669-4d2c-873b-c92b36fd79ab"));
            UpdaterRegistry.RegisterUpdater(updater, project, true);
            var filter = new LogicalOrFilter(new ElementClassFilter(typeof(FamilyInstance)), new ElementClassFilter(typeof(FamilySymbol)));
            foreach (var change in new[] { Element.GetChangeTypeAny(), Element.GetChangeTypeElementDeletion() }) UpdaterRegistry.AddTrigger(updater.GetUpdaterId(), project, filter, change);
            FamilyInstance member; FamilySymbol symbol;
            using (var tx = new Transaction(project, "QA connection source"))
            {
                tx.Start(); if (!project.LoadFamily(section, new FixtureLoadOptions(), out var family)) throw new InvalidOperationException("Cannot load section.");
                symbol = (FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First()); symbol.Activate(); project.Regenerate();
                member = project.Create.NewFamilyInstance(XYZ.Zero, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural); tx.Commit();
            }
            var created = JsonSerializer.Deserialize<JsonElement>(new UpsertConnectionNode().Execute(Args(new { node_key = "QA_dependent", preview = false, expected_revision = 0,
                spec = new { parts = new object[] {
                    new { key = "member", role = "member", element_id = member.Id.Value },
                    new { key = "plate", role = "plate", family_type_id = symbol.Id.Value, relative_to = "member", offset_mm = new[] { 100d, 0, 0 },
                        parameters = new[] { new { name = "Section_Width", unit = "mm", value_from = new { part_key = "member", name = "Section_Width", scope = "instance", scale = .5, offset_mm = 20 } } } }
                }, rules = Array.Empty<object>() } }), app));
            var plate = (FamilyInstance)project.GetElement(new ElementId(created.GetProperty("result").GetProperty("bindings").GetProperty("plate").GetInt64()));
            void Width(double expected)
            {
                var actual = plate.LookupParameter("Section_Width").AsDouble() * 304.8;
                var volume = Solids(plate).Sum(s => s.Volume) * Math.Pow(304.8, 3);
                if (Math.Abs(actual - expected) > .001 || Math.Abs(volume - expected * 400 * 1000) > 1) throw new InvalidOperationException("Dependent parameter/actual geometry disagree with width " + expected);
            }
            Width(170);
            using (var tx = new Transaction(project, "QA source dimension and position edit"))
            { tx.Start(); member.LookupParameter("Section_Width").Set(500 / 304.8); ElementTransformUtils.MoveElement(project, member.Id, new XYZ(1000 / 304.8, 0, 0)); tx.Commit(); }
            Width(270);
            if (Math.Abs(((LocationPoint)plate.Location).Point.X * 304.8 - 1100) > .001) throw new InvalidOperationException("Declared relative placement did not follow its source.");
            using (var tx = new Transaction(project, "QA independent managed-part edit")) { tx.Start(); plate.LookupParameter("Section_Width").Set(280 / 304.8); tx.Commit(); }
            Width(280);
            var manual = JsonSerializer.Deserialize<JsonElement>(new GetConnectionNode().Execute(Args(new { node_key = "QA_dependent" }), app));
            var record = manual;
            if (!record.GetProperty("needs_refresh").GetBoolean()) throw new InvalidOperationException("Independent managed edit was not flagged.");
            using (var tx = new Transaction(project, "QA deleted source")) { tx.Start(); project.Delete(member.Id); tx.Commit(); }
            Width(280);
            var deleted = JsonSerializer.Deserialize<JsonElement>(new GetConnectionNode().Execute(Args(new { node_key = "QA_dependent" }), app));
            project.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            return new { passed = true, file, created, manual_edit = manual, deleted_source = deleted, relative_x_mm = 1100,
                limitation = "Point-family dependency test; native structural beam/column placement, bores and capacity are separate cases." };
        }
        finally
        {
            NativeAcceptance.Target(null);
            if (updater != null && project is { IsValidObject: true } && UpdaterRegistry.IsUpdaterRegistered(updater.GetUpdaterId(),project)) UpdaterRegistry.UnregisterUpdater(updater.GetUpdaterId(), project);
            if (project is { IsValidObject: true }) project.Close(false);
        }
    }
}
