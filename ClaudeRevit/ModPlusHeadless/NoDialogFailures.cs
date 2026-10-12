#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace ModPlus.Headless;

// Scoped to the request's active document. Warnings become report entries;
// errors roll back the failing transaction instead of opening a failure dialog.
internal sealed class NoDialogFailures : IDisposable, IFailuresPreprocessor
{
    private readonly Application application;
    private readonly Document document;
    internal readonly List<object> Messages = new List<object>();
    internal bool HasErrors { get; private set; }
    private bool disposed;
    internal NoDialogFailures(Application app, Document doc)
    {
        application = app; document = doc;
        application.FailuresProcessing += OnFailures;
    }
    internal static void Configure(Transaction transaction)
    {
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
    }
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor) => Process(accessor);
    private void OnFailures(object sender, FailuresProcessingEventArgs args)
    {
        FailuresAccessor accessor = args.GetFailuresAccessor();
        if (!accessor.GetDocument().Equals(document)) return;
        args.SetProcessingResult(Process(accessor));
    }
    private FailureProcessingResult Process(FailuresAccessor accessor)
    {
        bool error = false;
        foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
        {
            FailureSeverity severity = message.GetSeverity();
            Messages.Add(new { severity = severity.ToString(), message = message.GetDescriptionText(), elementIds = message.GetFailingElementIds().Select(id => id.Value).ToArray() });
            if (severity == FailureSeverity.Warning) accessor.DeleteWarning(message);
            else if (severity != FailureSeverity.None) error = true;
        }
        if (!error) return FailureProcessingResult.Continue;
        HasErrors = true;
        FailureHandlingOptions options = accessor.GetFailureHandlingOptions();
        options.SetClearAfterRollback(true);
        accessor.SetFailureHandlingOptions(options);
        return FailureProcessingResult.ProceedWithRollBack;
    }
    public void Dispose()
    {
        if (disposed) return;
        application.FailuresProcessing -= OnFailures;
        disposed = true;
    }
}
