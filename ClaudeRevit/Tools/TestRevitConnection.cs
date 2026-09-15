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
    public string Description => "Checks Revit connectivity and reads the active document title. Creates and verifies a temporary level in a separate unsaved test document, rolls back that change, then closes only the test document. Never edits or saves the active user document.";
    public InputSchema InputSchema => new() { Properties = new Dictionary<string, JsonElement>(), Required = Array.Empty<string>() };
    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var activeTitle = app.ActiveUIDocument?.Document.Title;
        var test = app.Application.NewProjectDocument(UnitSystem.Metric);
        bool created = false, cleaned = false, closed = false;
        try
        {
            using var tx = new Transaction(test, "ClaudeRevit connection test");
            tx.Start();
            var level = Level.Create(test, 1.0);
            var id = level.Id;
            test.Regenerate();
            created = test.GetElement(id) is Level read && Math.Abs(read.Elevation - 1.0) < 1e-9;
            tx.RollBack();
            cleaned = test.GetElement(id) == null;
            if (!created || !cleaned) throw new InvalidOperationException("Isolated Revit write/rollback test failed.");
        }
        finally { closed = test.Close(false); }
        if (!closed) throw new InvalidOperationException("The temporary test document could not be closed.");
        return JsonSerializer.Serialize(new { revit_version = app.Application.VersionNumber,
            active_document = activeTitle, temporary_level_verified = created, rollback_verified = cleaned,
            temporary_document_closed = closed, user_document_modified = false });
    }
}
