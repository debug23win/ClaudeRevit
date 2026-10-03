namespace ClaudeRevit.Services;

// ToolWaitSeconds is the sum of MCP call waits (including Revit's event queue), not native
// execution time. Parallel call waits may overlap, so it must not be subtracted from total time.
public sealed class TurnTimings
{
    public double CatalogSeconds { get; set; }
    public double ContextSeconds { get; set; }
    public double ModelAndToolsSeconds { get; set; }
    public double ToolWaitSeconds { get; set; }
    public double QueueSeconds { get; set; }
    public double RevitExecutionSeconds { get; set; }
}
