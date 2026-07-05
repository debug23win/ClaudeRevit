using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Produces the same developer-facing learning report that is written automatically when
// Revit closes, but on demand and returned inline — so it can be pulled straight into the
// ClaudeRevit developer conversation to decide which recurring Dynamo/C# scripts to
// promote into dedicated native tools.
public class GenerateDiagnosticReport : IRevitTool
{
    public string Name => "generate_diagnostic_report";

    public string Description =>
        "Generates the learning/diagnostic report: aggregates the script journal into " +
        "recurring patterns (grouped by tool, engine and the model delta they produced) and " +
        "returns them as tool-promotion candidates — each recurring Dynamo/C# script that " +
        "could become a dedicated native tool, with a representative proven snippet. Use when " +
        "the user wants to review what the assistant has learned in this environment, or to " +
        "hand the report to the ClaudeRevit developer. The same report is written to " +
        "diagnostic_report.md automatically when Revit closes.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>(),
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var report = ExperienceStore.BuildDiagnosticReport();
        // Also persist a fresh copy so the on-disk file matches what was just shown.
        ExperienceStore.WriteDiagnosticReport();
        return JsonSerializer.Serialize(new { report, saved_to = ExperienceStore.ReportPath });
    }
}
