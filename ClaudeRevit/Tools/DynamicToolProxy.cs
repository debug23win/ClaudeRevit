using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Wraps a tool loaded from an external .cs file. A dynamic tool is arbitrary compiled code
// with full Revit API access — exactly as powerful as execute_csharp — so regardless of what
// the author class declares, the proxy forces it to be gated by the code-execution opt-in.
// That way a dynamic tool can never be offered to the model (or run) unless the user has
// ticked "Allow code execution", and it honours the per-run confirmation setting like the
// other code paths.
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

    // Forced on, not delegated: this is the whole security contract of dynamic tools.
    public bool RequiresCodeExecutionOptIn => true;
    public bool RequiresConfirmation => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app) =>
        _inner.Execute(input, app);
}
