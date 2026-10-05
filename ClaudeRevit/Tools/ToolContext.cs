using System.Threading;

namespace ClaudeRevit.Tools;

// The cancellation token of the tool call currently running on Revit's API thread.
//
// Ambient rather than a parameter on IRevitTool.Execute: threading a token through 187 tool
// signatures would touch every file to benefit the handful with unbounded loops, and every new
// tool would have to carry it. The dispatcher sets it around each call; a tool that loops over
// thousands of elements polls it.
//
// Honouring it matters because Stop previously only abandoned the WAIT: the tool kept running and
// kept editing the model for a turn nobody was listening to. Same for the MCP timeout — the client
// got an error while the tool carried on.
internal static class ToolContext
{
    [ThreadStatic] private static CancellationToken _ct;
    [ThreadStatic] private static bool _executing;
    public static bool IsExecuting => _executing;

    public static CancellationToken Current => _ct;

    [ThreadStatic] private static Autodesk.Revit.DB.Document? _document;
    [ThreadStatic] private static Action<string>? _progress;
    [ThreadStatic] private static long _lastProgressTick;
    [ThreadStatic] private static string? _lastProgressStage;
    public static Autodesk.Revit.UI.UIDocument? UiDocument(Autodesk.Revit.UI.UIApplication app) =>
        _document == null ? app.ActiveUIDocument : new Autodesk.Revit.UI.UIDocument(_document);
    public static void Set(CancellationToken ct, Autodesk.Revit.DB.Document? document = null, Action<string>? progress = null)
    { _ct = ct; _document = document; _progress = progress; _executing = true; _lastProgressTick = 0; _lastProgressStage = null; }
    public static void Clear() { _ct = default; _document = null; _progress = null; _executing = false; }
    public static void ReportProgress(int completed, int total, string stage)
    {
        ThrowIfCancelled();
        var now = Environment.TickCount64;
        if (stage == _lastProgressStage && completed < total && now - _lastProgressTick < 100) return;
        _lastProgressStage = stage; _lastProgressTick = now;
        _progress?.Invoke($"{stage}: {completed}/{total}");
    }

    // Call inside long loops. Throws OperationCanceledException, which the dispatcher already
    // reports as a cancelled call rather than a tool failure.
    public static void ThrowIfCancelled() => _ct.ThrowIfCancellationRequested();

    public static bool IsCancelled => _ct.IsCancellationRequested;
}
