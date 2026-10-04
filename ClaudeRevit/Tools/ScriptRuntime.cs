namespace ClaudeRevit.Tools;

// Public so compiled snippets can cooperate with Stop without unsafe thread abortion.
public static class ScriptRuntime
{
    public static void CheckCancellation() => ToolContext.ThrowIfCancelled();
    public static void ReportProgress(int completed, int total, string stage) => ToolContext.ReportProgress(completed,total,stage);
}
