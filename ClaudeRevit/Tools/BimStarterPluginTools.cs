using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

internal sealed record BimStarterButton(string ClassName, string Name, string Title, string Panel, string? CommandId, bool CanPost);
internal static class BimStarterRibbon
{
    public static List<BimStarterButton> Buttons(UIApplication app)
    {
        var result = new List<BimStarterButton>();
        List<RibbonPanel> panels;
        try { panels = app.GetRibbonPanels("BIM-STARTER"); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { return result; }
        void Add(RibbonItem item, RibbonPanel panel)
        {
            if (item is PushButton button)
            {
                var name = $"CustomCtrl_%CustomCtrl_%BIM-STARTER%{panel.Name}%{button.Name}";
                RevitCommandId? command = null;
                try { command = RevitCommandId.LookupCommandId(name); } catch { }
                var canPost = command != null && app.CanPostCommand(command);
                result.Add(new(button.ClassName, button.Name, button.ItemText, panel.Name, command == null ? null : name, canPost));
            }
            else if (item is PulldownButton pull) foreach (var child in pull.GetItems()) Add(child, panel);
            else if (item is SplitButton split) foreach (var child in split.GetItems()) Add(child, panel);
        }
        foreach (var panel in panels) foreach (var item in panel.GetItems()) Add(item, panel);
        return result;
    }
}

public sealed class GetBimStarterTools : IRevitTool
{
    public string Name => "get_bimstarter_tools";
    public string Description => "Search all 63 external commands in the verified BIMStarter source snapshot, with native alternatives and explicit partial/interactive coverage. Also inspect the live BIM-STARTER ribbon to find installed commands and current postability. Cloud/account commands remain interactive; this tool never reads plugin credentials.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["query"] = NativeToolUtil.Field("string", "Substring in class/title/tool/operation."),
        ["offset"] = NativeToolUtil.Field("integer", "Default 0."),
        ["limit"] = NativeToolUtil.Field("integer", "Default 25, 1..100.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var query = NativeToolUtil.Text(input, "query");
        var offset = Math.Max(0, ToolInput.OptionalInt(input, "offset") ?? 0);
        var limit = Math.Clamp(ToolInput.OptionalInt(input, "limit") ?? 25, 1, 100);
        var live = BimStarterRibbon.Buttons(app);
        var matches = Services.BimStarterKnowledge.Commands.Where(c => query.Length == 0 || c.GetRawText().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Services.Json.Serialize(new
        {
            repository = Services.BimStarterKnowledge.Source.GetProperty("repository").GetString(),
            commit = Services.BimStarterKnowledge.Source.GetProperty("commit").GetString(),
            author = "Aleksandr Zuev", total = matches.Length, installed_buttons = live.Count, offset,
            commands = matches.Skip(offset).Take(limit).Select(c => new { reference = c, installed = live.Where(b => b.ClassName == c.GetProperty("id").GetString()).ToArray() }),
            next_offset = offset + limit < matches.Length ? (int?)(offset + limit) : null,
            note = "Native workflows are independently implemented. Partial workflow is not a full port. Postability only queues an interactive command, not its completion; inspect its result after the user closes its dialog."
        });
    }
}

public sealed class RunBimStarterCommand : IRevitTool
{
    public string Name => "run_bimstarter_command";
    public string Description => "Queue an installed BIMStarter ribbon command using native Revit PostCommand. Default preview=true only checks. Set preview=false to post; the plugin may open a dialog and require user input. Returns posted, NEVER completed. Wait for the user to finish the dialog, then verify model changes. No credentials or DLL reflection.";
    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["command"] = NativeToolUtil.Field("string", "Exact installed ClassName from get_bimstarter_tools."),
        ["element_ids"] = NativeToolUtil.Array("integer", "Optional selection to pass to the plugin; max 5000."),
        ["command_id"] = NativeToolUtil.Field("string", "Optional exact Revit journal command ID if the ribbon-derived ID is unavailable. Must identify this live button on BIM-STARTER."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true. False queues the interactive command.")
    }, "command");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var name = ToolInput.RequiredString(input, "command");
        var candidates = BimStarterRibbon.Buttons(app).Where(b => b.ClassName == name).ToArray();
        if (candidates.Length != 1) throw new ToolInputException("An unambiguous installed BIMStarter button was not found. Inspect get_bimstarter_tools.");
        var button = candidates[0];
        var id = NativeToolUtil.Text(input, "command_id", button.CommandId ?? "");
        if (!id.StartsWith("CustomCtrl_", StringComparison.Ordinal) || !id.Contains("BIM-STARTER", StringComparison.Ordinal) || !id.EndsWith("%" + button.Name, StringComparison.Ordinal))
            throw new ToolInputException("Cannot resolve this live button's command ID. Use its exact BIM-STARTER journal ID or the native alternative.");
        var command = RevitCommandId.LookupCommandId(id);
        if (command == null || !app.CanPostCommand(command)) throw new ToolInputException("Revit cannot post this command now. Close active dialogs or use its native alternative.");
        var preview = NativeToolUtil.Preview(input);
        if (!preview)
        {
            if (ToolDispatcher.ForceSuppress) throw new ToolInputException("Interactive plugin commands cannot run in an unattended benchmark. Use the native workflow.");
            var uidoc = app.ActiveUIDocument ?? throw new ToolInputException("Open a model before posting a plugin command.");
            if (input.TryGetValue("element_ids", out var e))
            {
                var ids = NativeToolUtil.Ids(e, 5000);
                foreach (var element in ids) NativeToolUtil.Element(uidoc.Document, element.Value);
                uidoc.Selection.SetElementIds(ids);
            }
            ToolContext.ThrowIfCancelled(); app.PostCommand(command);
        }
        return Services.Json.Serialize(new { preview, posted = !preview, completed = false, command = name, command_id = id,
            next = "Wait for the plugin dialog to finish. Inspect actual model changes before continuing; do not infer success from posted=true." });
    }
}
