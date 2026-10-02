using System.Text.Json;
using System.IO;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class OpenFamilyEditor : IRevitTool
{
    public string Name => "open_family_editor";
    public string Description => "Open/activate an existing RFA, create one from an RFT, or edit a loaded family into a new saved RFA copy. action=open/create/edit. create/edit require a new output_path; never overwrite. Opening ends the previous document's grouped undo step; the agent follows the active family document.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public bool InvalidatesCatalog => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["action"] = NativeToolUtil.Field("string", "open, create or edit."),
        ["file_path"] = NativeToolUtil.Field("string", "Existing .rfa for open; .rft template for create."),
        ["family_id"] = NativeToolUtil.Field("integer", "Loaded Family for edit."),
        ["output_path"] = NativeToolUtil.Field("string", "New absolute .rfa path for create/edit; parent folder must exist.")
    }, "action");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var action = ToolInput.RequiredString(input, "action");
        var path = NativeToolUtil.Text(input, "file_path");
        if (action == "open")
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path) || !path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) throw new ToolInputException("Supply an existing .rfa path.");
        }
        else if (action is "create" or "edit")
        {
            var output = ToolInput.RequiredString(input, "output_path");
            if (!Path.IsPathFullyQualified(output)) throw new ToolInputException("output_path must be absolute.");
            output = Path.GetFullPath(output);
            if (!output.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) || File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
                throw new ToolInputException("Use a new .rfa file in an existing folder.");
            Document? family = null;
            try
            {
                if (action == "create")
                {
                    if (!File.Exists(path) || !path.EndsWith(".rft", StringComparison.OrdinalIgnoreCase)) throw new ToolInputException("Supply an existing .rft template.");
                    family = app.Application.NewFamilyDocument(path);
                }
                else
                {
                    var source = NativeToolUtil.Doc(app);
                    var loaded = NativeToolUtil.Element(source, ToolInput.RequiredLong(input, "family_id")) as Family ?? throw new ToolInputException("family_id must be a loaded Family.");
                    if (!loaded.IsEditable || loaded.IsInPlace) throw new ToolInputException("Family is not editable.");
                    family = source.EditFamily(loaded);
                }
                ToolContext.ThrowIfCancelled();
                family.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = false });
                path = output;
            }
            finally { if (family is { IsValidObject: true }) family.Close(false); }
        }
        else throw new ToolInputException("action must be open/create/edit.");
        ToolContext.ThrowIfCancelled();
        var active = app.OpenAndActivateDocument(path).Document;
        if (!active.IsFamilyDocument) throw new InvalidOperationException("Activated file is not a family.");
        return Services.Json.Serialize(new { action, path, family = active.OwnerFamily.Name, document_key = Services.DocumentSessions.Key(active),
            note = action == "edit" ? "Editing a copy; use reload_family_into_document to load the finished RFA into a project." : null });
    }
}
