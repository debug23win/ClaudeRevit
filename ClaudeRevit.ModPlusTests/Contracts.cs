// Only Revit is replaced: the production tools, catalog, DTO reader and Anthropic schemas run
// unchanged. These contracts never ship in the add-in and cannot execute ModPlus assemblies.
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
namespace Autodesk.Revit.DB
{
    public sealed record ElementId(long Value);
    public class Element { }
    public class Document
    {
        public string Title => "isolated";
        public Element? GetElement(ElementId id) => id.Value is 1 or 2 ? new Element() : null;
    }
}
namespace Autodesk.Revit.ApplicationServices { public class Application { public string VersionNumber { get; set; } = "2027"; } }
namespace Autodesk.Revit.Exceptions { public class ArgumentException : System.ArgumentException { } }
namespace Autodesk.Revit.UI.Selection
{
    public class Selection
    {
        private List<Autodesk.Revit.DB.ElementId> _ids = [new(1)];
        public ICollection<Autodesk.Revit.DB.ElementId> GetElementIds() => _ids.ToArray();
        public void SetElementIds(ICollection<Autodesk.Revit.DB.ElementId> ids) => _ids = ids.ToList();
    }
}
namespace Autodesk.Revit.UI
{
    public class UIDocument
    {
        public Autodesk.Revit.DB.Document Document { get; set; } = new();
        public Selection.Selection Selection { get; } = new();
    }
    public class UIApplication
    {
        public Autodesk.Revit.ApplicationServices.Application Application { get; } = new();
        public UIDocument? ActiveUIDocument { get; set; } = new();
        public List<RibbonPanel> Panels { get; } = new();
        public List<string> Posted { get; } = new();
        public bool AllowPost { get; set; } = true;
        public bool FailPost { get; set; }
        public List<RibbonPanel> GetRibbonPanels(string tab) => tab == "ModPlus" ? Panels : [];
        public bool CanPostCommand(RevitCommandId id) => AllowPost;
        public void PostCommand(RevitCommandId id) { if(FailPost)throw new InvalidOperationException("Simulated posting failure."); Posted.Add(id.Name); }
    }
    public sealed record RevitCommandId(string Name) { public static RevitCommandId? LookupCommandId(string id) => string.IsNullOrEmpty(id) ? null : new(id); }
    public class RibbonItem { public string Name { get; set; } = ""; public string ItemText { get; set; } = ""; }
    public class PushButton : RibbonItem { public string ClassName { get; set; } = ""; }
    public class RibbonPanel
    {
        public string Name { get; set; } = "Utilities";
        public List<RibbonItem> Items { get; } = new();
        public List<RibbonItem> GetItems() => Items;
    }
    public class PulldownButton : RibbonItem { public List<PushButton> Items { get; } = new(); public List<PushButton> GetItems() => Items; }
    public class SplitButton : RibbonItem { public List<PushButton> Items { get; } = new(); public List<PushButton> GetItems() => Items; }
}
namespace ClaudeRevit.Services
{
    public static class DocumentSessions { public static bool Same(Autodesk.Revit.DB.Document? a, Autodesk.Revit.DB.Document? b) => a != null && ReferenceEquals(a, b); }
}
namespace ClaudeRevit.Tools
{
    public static class ToolDispatcher { public static bool ForceSuppress; }
    public static class ToolContext
    {
        public static Autodesk.Revit.UI.UIDocument? Bound;
        public static Autodesk.Revit.UI.UIDocument? UiDocument(Autodesk.Revit.UI.UIApplication app) => Bound ?? app.ActiveUIDocument;
        public static void ThrowIfCancelled() { }
    }
    public static class NativeToolUtil
    {
        public static JsonElement Field(string type, string description) => JsonSerializer.SerializeToElement(new { type, description });
        public static JsonElement Array(string type, string description) => JsonSerializer.SerializeToElement(new { type="array", items=new { type }, description });
        public static InputSchema Schema(Dictionary<string, JsonElement> fields, params string[] required) => new() { Properties=fields, Required=required.ToList() };
        public static string Text(IReadOnlyDictionary<string, JsonElement> input, string key, string fallback="") => input.TryGetValue(key,out var v) && v.ValueKind==JsonValueKind.String ? v.GetString()??fallback : fallback;
        public static bool Preview(IReadOnlyDictionary<string, JsonElement> input) => !input.ContainsKey("preview") || !input.TryGetValue("preview",out var v) || v.ValueKind==JsonValueKind.Null || ToolInput.Flag(input,"preview");
        public static List<Autodesk.Revit.DB.ElementId> Ids(JsonElement value,int max)
        {
            var ids=value.EnumerateArray().Select(x=>new Autodesk.Revit.DB.ElementId(x.GetInt64())).Distinct().ToList();
            if(ids.Count<1||ids.Count>max)throw new ToolInputException("Invalid element_ids count.");
            return ids;
        }
        public static Autodesk.Revit.DB.Element Element(Autodesk.Revit.DB.Document doc,long id) => doc.GetElement(new(id))??throw new ToolInputException("Missing element.");
    }
}
