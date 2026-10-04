using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class TestRevitConnection : IRevitTool
{
    public string Name => "test_revit_connection";
    public string Description => "Checks Revit connectivity and the active document title. In a separate unsaved test project, verifies a level and element type, filtered all-element census and nested transaction-group rollback used by the benchmark. Closes only the test document; never edits or saves the active user document.";
    public InputSchema InputSchema => new() { Properties = new Dictionary<string, JsonElement>(), Required = Array.Empty<string>() };
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var activeTitle = ToolContext.UiDocument(app)?.Document.Title;
        var test = app.Application.NewProjectDocument(UnitSystem.Metric);
        bool created = false, cleaned = false, closed = false, censusVerified = false, nestedRollback = false;
        try
        {
            var baseline = BenchmarkModelProbe.AllElementIds(test).ToHashSet();
            using var outer = new TransactionGroup(test, "ClaudeRevit benchmark reset check");
            outer.Start();
            var (ids, _) = NativeToolUtil.Commit(test, "ClaudeRevit nested connection check", false, () =>
            {
                var level = Level.Create(test, 1.0);
                var cover = Autodesk.Revit.DB.Structure.RebarCoverType.Create(test, "Claude benchmark cover check", 0.1);
                test.Regenerate();
                return (Level: level.Id.Value, Type: cover.Id.Value);
            });
            created = test.GetElement(new ElementId(ids.Level)) is Level read && Math.Abs(read.Elevation - 1.0) < 1e-9;
            var census = BenchmarkModelProbe.AllElementIds(test);
            censusVerified = census.Contains(ids.Level) && census.Contains(ids.Type);
            nestedRollback = outer.RollBack() == TransactionStatus.RolledBack;
            cleaned = test.GetElement(new ElementId(ids.Level)) == null && test.GetElement(new ElementId(ids.Type)) == null &&
                baseline.SetEquals(BenchmarkModelProbe.AllElementIds(test));
            if (!created || !censusVerified || !nestedRollback || !cleaned) throw new InvalidOperationException("Isolated Revit census/nested rollback test failed.");
        }
        finally { closed = test.Close(false); }
        if (!closed) throw new InvalidOperationException("The temporary test document could not be closed.");
        return JsonSerializer.Serialize(new { revit_version = app.Application.VersionNumber,
            active_document = activeTitle, temporary_level_verified = created, rollback_verified = cleaned,
            filtered_instance_and_type_census_verified = censusVerified, nested_group_rollback_verified = nestedRollback,
            temporary_document_closed = closed, user_document_modified = false });
    }
}
