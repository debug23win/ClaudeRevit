using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public interface IRevitTool
{
    string Name { get; }
    string Description { get; }
    InputSchema InputSchema { get; }
    bool RequiresTransaction { get; }

    // When true, the chat pane shows an Allow/Deny prompt (with the tool's input) before
    // the call runs — but only if the user re-enabled confirmations in settings (off by
    // default). Use for destructive or arbitrary-code operations.
    bool RequiresConfirmation => false;

    // When true, the tool is only offered to Claude (and only runs) if the user has
    // ticked "Allow code execution" in settings. Arbitrary-code tools set this.
    bool RequiresCodeExecutionOptIn => false;

    // Arbitrary-code tools (journaled with their model delta by ScriptJournal).
    bool IsScriptTool => false;

    // Tools that change the document while RequiresTransaction is false (they manage
    // transactions themselves) — the dispatcher must still invalidate caches after them.
    bool MutatesWithoutTransaction => false;

    // Whether this tool can change the PROJECT CATALOG: the families, types and materials the
    // model can choose from. Rebuilding that catalog is around a dozen full collector passes over
    // the document on Revit's UI thread, and it used to be thrown away after ANY transactional
    // tool — so moving a wall or setting a parameter, which cannot add a type, paid for a full
    // rebuild. Only loading families and creating, duplicating or renaming types belong here.
    //
    // Script tools default to true: arbitrary code can create anything, and the alternative is a
    // model working from a catalog that no longer matches the document.
    bool InvalidatesCatalog => IsScriptTool;

    string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app);
}
