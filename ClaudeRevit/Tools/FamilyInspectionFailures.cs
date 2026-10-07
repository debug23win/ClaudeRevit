using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace ClaudeRevit.Tools;

// Capture upgrade warnings only in new background RFA documents. User-opened
// documents are excluded. Errors abort the operation; never resolve an error
// by deleting geometry just to get an inspection to finish.
internal sealed class FamilyInspectionFailures : IDisposable
{
    [ThreadStatic] private static FamilyInspectionFailures? _current;
    private readonly FamilyInspectionFailures? _previous;
    private readonly Application _application;
    private readonly HashSet<Document> _existing;
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public FamilyInspectionFailures(Application application)
    {
        _application = application;
        _existing = application.Documents.Cast<Document>().ToHashSet();
        _previous = _current; _current = this;
        // No subscription of its own: App.OnFailuresProcessing, registered for the add-in's whole
        // lifetime, calls Handle first on every event. Subscribing here as well processed each
        // event twice — every warning recorded twice, and a second DeleteWarning on a message the
        // first pass had already removed.
    }
    public static bool Handle(FailuresProcessingEventArgs e)
    {
        var scope = _current;
        if (scope == null) return false;
        var accessor = e.GetFailuresAccessor();
        var document = accessor.GetDocument();
        if (scope._existing.Contains(document) || !document.IsFamilyDocument) return false;
        var messages = accessor.GetFailureMessages();
        bool error = false;
        foreach (var message in messages)
        {
            if (message.GetSeverity() == FailureSeverity.Warning)
            {
                scope.Warnings.Add(message.GetDescriptionText());
                accessor.DeleteWarning(message);
            }
            else { scope.Errors.Add(message.GetDescriptionText()); error = true; }
        }
        if (error) e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
        else if (messages.Count > 0) e.SetProcessingResult(FailureProcessingResult.Continue);
        return true;
    }
    public void Dispose()
    {
        _current = _previous;
    }
}
