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

    public static CancellationToken Current => _ct;

    public static void Set(CancellationToken ct) => _ct = ct;
    public static void Clear() => _ct = default;

    // Call inside long loops. Throws OperationCanceledException, which the dispatcher already
    // reports as a cancelled call rather than a tool failure.
    public static void ThrowIfCancelled() => _ct.ThrowIfCancellationRequested();

    public static bool IsCancelled => _ct.IsCancellationRequested;
}
