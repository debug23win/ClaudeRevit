using System.Threading;
using System.Collections.Generic;
using System.Linq;
using Anthropic.Helpers.Beta;
using Anthropic.Models.Beta.Messages;

namespace ClaudeRevit.Tools;

public class ToolRegistry
{
    private static ToolRegistry? _instance;
    public static ToolRegistry Instance => _instance ??= new ToolRegistry();

    // Guards the map: built-ins register on startup, but dynamic tools (DynamicToolLoader)
    // register/unregister from the Revit API thread mid-session while ChatService reads the
    // list from an async continuation to build the per-turn tool defs.
    private readonly object _gate = new();
    private readonly Dictionary<string, IRevitTool> _tools = new();

    // Bumped on every change, so caches derived from the tool set (the MCP tool index) can tell
    // they are stale — custom tools register in the background after startup, and save_tool adds
    // more mid-session.
    private int _version;
    public int Version => Volatile.Read(ref _version);

    public void Register(IRevitTool tool)
    {
        lock (_gate) { _tools[tool.Name] = tool; _version++; }
    }

    public bool Unregister(string name)
    {
        lock (_gate) { var removed = _tools.Remove(name); if (removed) _version++; return removed; }
    }

    public IRevitTool? Get(string name)
    {
        lock (_gate) return _tools.TryGetValue(name, out var tool) ? tool : null;
    }

    // Snapshot so callers iterate a stable list even if a dynamic tool is (un)registered
    // concurrently.
    public IReadOnlyCollection<IRevitTool> All
    {
        get { lock (_gate) return _tools.Values.ToList(); }
    }

    public IReadOnlyList<BetaTool> BuildToolDefinitions()
    {
        lock (_gate)
            return _tools.Values.Select(t => new BetaTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.InputSchema
            }).ToList();
    }
}
