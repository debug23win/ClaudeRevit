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
        return new { passed = checks.Count(c => (bool)c.GetType().GetProperty("ok")!.GetValue(c)!), total = checks.Count, checks };
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
