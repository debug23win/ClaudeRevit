using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Compatibility wrapper for saved tools: mark code operations so native group filters
// and optional native-operation confirmation cannot hide or block them.
internal sealed class DynamicToolProxy : IRevitTool
{
    private readonly IRevitTool _inner;
    public DynamicToolProxy(IRevitTool inner) => _inner = inner;

    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public InputSchema InputSchema => _inner.InputSchema;

    // Delegate the transaction/mutation contract to the author so a dynamic tool can declare
    // it needs the dispatcher's managed transaction (and undo grouping).
    public bool RequiresTransaction => _inner.RequiresTransaction;
    public bool MutatesWithoutTransaction => _inner.MutatesWithoutTransaction;

    // Always mark saved source tools as code, regardless of their author declaration.
    public bool RequiresCodeExecutionOptIn => true;
    public bool RequiresConfirmation => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app) =>
        _inner.Execute(input, app);
}
