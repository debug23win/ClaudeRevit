// Match Revit's documented native equality: separate managed wrappers can refer to
// one open document. This deliberately reproduces the reference-identity regression.
namespace Autodesk.Revit.DB
{
    public sealed class DocumentState
    {
        public string PathName = "";
        public bool Valid = true;
        public bool Cloud, Workshared;
        public Guid ProjectId, ModelId, CentralId;
    }
    public sealed class Document(DocumentState state)
    {
        public bool IsValidObject => state.Valid;
        public string PathName => state.PathName;
        public bool IsModelInCloud => state.Cloud;
        public bool IsWorkshared => state.Workshared;
        public Guid WorksharingCentralGUID => state.CentralId;
        public ModelPath GetCloudModelPath() => new(state.ProjectId, state.ModelId);
        public override bool Equals(object? other) => other is Document doc && ReferenceEquals(state, doc.State);
        private DocumentState State => state;
        // Identical hashes for distinct documents also catch hash-only implementations.
        public override int GetHashCode() => 7;
    }
    public sealed class ModelPath(Guid project, Guid model)
    {
        public Guid GetProjectGUID() => project;
        public Guid GetModelGUID() => model;
    }
    public sealed class View(Document document) { public Document Document => document; }
}
namespace Autodesk.Revit.UI.Events
{
    public sealed class ViewActivatedEventArgs(Autodesk.Revit.DB.View view) : EventArgs
    { public Autodesk.Revit.DB.View CurrentActiveView => view; }
    public sealed class IdlingEventArgs : EventArgs { }
}
namespace Autodesk.Revit.UI
{
    public sealed class UIDocument(Autodesk.Revit.DB.DocumentState state)
    { public Autodesk.Revit.DB.Document Document => new(state); }
    public sealed class UIApplication { public UIDocument? ActiveUIDocument { get; set; } }
    public sealed class UIControlledApplication
    {
        public event EventHandler<Events.ViewActivatedEventArgs>? ViewActivated;
        public event EventHandler<Events.IdlingEventArgs>? Idling;
        public void Idle(UIApplication app) => Idling?.Invoke(app, new());
        public void Activate(Autodesk.Revit.DB.DocumentState state) => ViewActivated?.Invoke(this, new(new(new(state))));
    }
}
internal static class DocumentHarness
{
    private static readonly Dictionary<string, Autodesk.Revit.DB.DocumentState> Documents = new();
    public static void Change(string name, bool managed)
    {
        if (!Documents.TryGetValue(name, out var state))
            Documents[name] = state = new() { PathName = name };
        ClaudeRevit.Tools.ToolContext.IsExecuting = managed;
        try { ClaudeRevit.Services.DocumentSessions.Update(new(state)); }
        finally { ClaudeRevit.Tools.ToolContext.IsExecuting = false; }
    }
}
