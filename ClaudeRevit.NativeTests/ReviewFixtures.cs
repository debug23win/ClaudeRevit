using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Tools;

namespace ClaudeRevit.NativeTests;

// Live checks for the v3.8.6 review fixes that could only be reasoned about without Revit.
// Each check uses its own disposable metric project and reports its own pass/fail, so one
// failure does not hide the others.
internal static class ReviewFixtures
{
    public static object Run(UIApplication app)
    {
        var checks = new List<object>();
        void Check(string name, Func<object> body)
        {
            try { checks.Add(new { name, ok = true, detail = body() }); }
            catch (Exception ex) { checks.Add(new { name, ok = false, detail = (object)ex.Message }); }
        }
        Check("change_element_type_actually_changes_type", () => WithProject(app, ChangeTypeSticks));
        Check("execute_csharp_result_serialization_never_throws", () => WithProject(app, ScriptResultsSerialize));
        Check("set_parameter_reaches_material_named_like_a_type", () => WithProject(app, MaterialBeatsSameNamedType));
        Check("audit_total_wall_thickness", () => WithProject(app, TotalThickness));
        Check("audit_derived_length_internal_units", () => WithProject(app, DerivedLength));
        Check("audit_material_density_7850", () => WithProject(app, MaterialDensity));
        Check("audit_placed_unbounded_room_survives_cleanup", () => WithProject(app, PlacedRoom));
        return new { passed = checks.Count(c => (bool)c.GetType().GetProperty("ok")!.GetValue(c)!), total = checks.Count, checks };
    }

    private static object TotalThickness(UIApplication app, Document doc)
    {
        using var tx = new Transaction(doc, "QA total width"); tx.Start();
        var original = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
        var basis = (WallType)original.Duplicate("QA_Multilayer_Base");
        var structure = CompoundStructure.CreateSimpleCompoundStructure(new List<CompoundStructureLayer>
        {
            new(20 / 304.8, MaterialFunctionAssignment.Finish1, ElementId.InvalidElementId),
            new(200 / 304.8, MaterialFunctionAssignment.Structure, ElementId.InvalidElementId),
            new(30 / 304.8, MaterialFunctionAssignment.Finish2, ElementId.InvalidElementId)
        });
        structure.SetNumberOfShellLayers(ShellLayerType.Exterior, 1);
        structure.SetNumberOfShellLayers(ShellLayerType.Interior, 1);
        basis.SetCompoundStructure(structure);
        using var result = JsonDocument.Parse(new CreateWallType().Execute(SyntheticFixtures.Args(new { name = "QA_Total_300", based_on = basis.Name, thickness_mm = 300 }), app));
        var created = (WallType)doc.GetElement(new ElementId(result.RootElement.GetProperty("id").GetInt64()));
        doc.Regenerate();
        var actual = created.Width * 304.8;
        if (Math.Abs(actual - 300) > .001) throw new InvalidOperationException($"Expected total 300 mm, got {actual}.");
        tx.RollBack(); return new { expected_mm = 300, actual_mm = actual };
    }

    private static object DerivedLength(UIApplication app, Document doc)
    {
        using var tx = new Transaction(doc, "QA derive length"); tx.Start();
        var (wall, _, _) = WallWithTwoTypes(doc, "QA_Derive_Type");
        doc.Regenerate();
        var parameter = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
        var expectedFeet = ((LocationCurve)wall.Location).Curve.Length;
        using var result = JsonDocument.Parse(new DeriveParameters().Execute(SyntheticFixtures.Args(new
        {
            element_ids = new[] { wall.Id.Value }, parameter_name = parameter.Definition.Name, source = "length_mm", round = 6
        }), app));
        doc.Regenerate();
        var actualFeet = parameter.AsDouble();
        if (Math.Abs(actualFeet - expectedFeet) > 1e-7) throw new InvalidOperationException($"Expected {expectedFeet} ft, got {actualFeet} ft.");
        tx.RollBack(); return new { expected_feet = expectedFeet, actual_feet = actualFeet };
    }

    private static object MaterialDensity(UIApplication app, Document doc)
    {
        using var tx = new Transaction(doc, "QA density"); tx.Start();
        var materialId = Material.Create(doc, "QA_Steel_7850");
        var material = (Material)doc.GetElement(materialId);
        var asset = new StructuralAsset("QA_Steel_Asset", StructuralAssetClass.Metal) { Density = 7850 * Math.Pow(.3048, 3) };
        material.SetMaterialAspectByPropertySet(MaterialAspect.Structural, PropertySetElement.Create(doc, asset).Id);
        var side = 1 / .3048;
        var loop = CurveLoop.Create(new List<Curve>
        {
            Line.CreateBound(XYZ.Zero, new XYZ(side, 0, 0)), Line.CreateBound(new XYZ(side, 0, 0), new XYZ(side, side, 0)),
            Line.CreateBound(new XYZ(side, side, 0), new XYZ(0, side, 0)), Line.CreateBound(new XYZ(0, side, 0), XYZ.Zero)
        });
        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, side,
            new SolidOptions(materialId, ElementId.InvalidElementId));
        var shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel)); shape.SetShape(new GeometryObject[] { solid });
        doc.Regenerate();
        using var result = JsonDocument.Parse(new CalculateWeight().Execute(SyntheticFixtures.Args(new { element_ids = new[] { shape.Id.Value } }), app));
        var mass = result.RootElement.GetProperty("total_kg").GetDouble();
        if (Math.Abs(mass - 7850) > .02) throw new InvalidOperationException($"A 1 m3 steel solid must weigh 7850 kg, got {mass}.");
        tx.RollBack(); return new { volume_m3 = 1, expected_kg = 7850, actual_kg = mass };
    }

    private static object PlacedRoom(UIApplication app, Document doc)
    {
        using var tx = new Transaction(doc, "QA unbounded room"); tx.Start();
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
        var room = doc.Create.NewRoom(level, new UV(1, 1)); doc.Regenerate();
        if (room.Location == null || room.Area > 0) throw new InvalidOperationException("Fixture must be placed and unbounded.");
        var id = room.Id;
        var result = new CleanModel().Execute(SyntheticFixtures.Args(new { targets = new[] { "unplaced_rooms" }, apply = true }), app);
        if (doc.GetElement(id) == null) throw new InvalidOperationException("Cleanup deleted a placed room.");
        tx.RollBack(); return new { preserved = true, result };
    }

    private static object WithProject(UIApplication app, Func<UIApplication, Document, object> body)
    {
        Document? project = null;
        try
        {
            project = app.Application.NewProjectDocument(UnitSystem.Metric);
            NativeAcceptance.Target(project);
            return body(app, project);
        }
        finally
        {
            NativeAcceptance.Target(null);
            if (project is { IsValidObject: true }) project.Close(false);
        }
    }

    private static (Wall Wall, WallType Original, WallType Other) WallWithTwoTypes(Document doc, string otherName)
    {
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
        var original = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
        var other = (WallType)original.Duplicate(otherName);
        var wall = Wall.Create(doc, Line.CreateBound(XYZ.Zero, new XYZ(10, 0, 0)), original.Id, level.Id, 10, 0, false, false);
        return (wall, original, other);
    }

    // The v3.8.6 bug: the restore loop wrote the instance's own "Type" parameter back to the old
    // type, so the tool reported success with nothing changed.
    private static object ChangeTypeSticks(UIApplication app, Document doc)
    {
        Wall wall; WallType original, other;
        using (var tx = new Transaction(doc, "QA wall")) { tx.Start(); (wall, original, other) = WallWithTwoTypes(doc, "QA_Other_Type"); tx.Commit(); }
        string raw;
        using (var tx = new Transaction(doc, "QA change type"))
        {
            tx.Start();
            raw = new ChangeElementType().Execute(SyntheticFixtures.Args(new { element_ids = new[] { wall.Id.Value }, new_type_id = other.Id.Value }), app);
            tx.Commit();
        }
        var now = ((Wall)doc.GetElement(wall.Id)).GetTypeId();
        if (now != other.Id) throw new InvalidOperationException($"Wall is on type {now.Value}, expected {other.Id.Value}. Tool said: {raw}");
        return new { from = original.Id.Value, to = other.Id.Value, result = JsonSerializer.Deserialize<JsonElement>(raw) };
    }

    // Values whose getters throw or cycle (Curve, Document) used to escape serialization inside
    // the transaction and roll back the snippet's work.
    private static object ScriptResultsSerialize(UIApplication app, Document doc)
    {
        var serialize = typeof(ExecuteCSharp).GetMethod("SerializeResult", BindingFlags.NonPublic | BindingFlags.Static)!;
        var outputs = new List<string>();
        foreach (var value in new object?[] { Line.CreateBound(XYZ.Zero, new XYZ(1, 2, 3)), doc, new { count = 3, line = Line.CreateBound(XYZ.Zero, XYZ.BasisX) }, null })
        {
            var json = (string)serialize.Invoke(null, new[] { value })!;
            using var parsed = JsonDocument.Parse(json);
            if (!parsed.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Serialization reported failure: " + json);
            outputs.Add(json.Length > 200 ? json[..200] + "…" : json);
        }
        return outputs;
    }

    // With a wall type and a material both called "Бетон", a material parameter must resolve to
    // the material (v3.8.6 stopped at the first group with a name match).
    private static object MaterialBeatsSameNamedType(UIApplication app, Document doc)
    {
        Wall wall; ElementId materialId;
        var app0 = doc.Application; var previous = app0.SharedParametersFilename;
        var path = Path.Combine(Path.GetTempPath(), "ClaudeRevit-qa-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\nGROUP\t1\tQA\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\n", Encoding.UTF8);
            app0.SharedParametersFilename = path;
            using var tx = new Transaction(doc, "QA material parameter");
            tx.Start();
            (wall, _, _) = WallWithTwoTypes(doc, "Бетон");
            materialId = Material.Create(doc, "Бетон");
            var file = app0.OpenSharedParameterFile();
            var definition = file.Groups.get_Item("QA").Definitions.Create(new ExternalDefinitionCreationOptions("QA_Material", SpecTypeId.Reference.Material));
            var categories = app0.Create.NewCategorySet(); categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Walls));
            if (!doc.ParameterBindings.Insert(definition, app0.Create.NewInstanceBinding(categories), GroupTypeId.Data)) throw new InvalidOperationException("Binding failed.");
            tx.Commit();
        }
        finally { app0.SharedParametersFilename = previous; try { File.Delete(path); } catch { } }

        string raw;
        using (var tx = new Transaction(doc, "QA set material by name"))
        {
            tx.Start();
            raw = new SetParameter().Execute(SyntheticFixtures.Args(new { element_id = wall.Id.Value, parameter_name = "QA_Material", value = "Бетон" }), app);
            tx.Commit();
        }
        var set = ((Wall)doc.GetElement(wall.Id)).LookupParameter("QA_Material").AsElementId();
        if (set != materialId) throw new InvalidOperationException($"Parameter holds {set.Value}, expected material {materialId.Value}. Tool said: {raw}");
        return new { material = materialId.Value, result = JsonSerializer.Deserialize<JsonElement>(raw) };
    }
}
