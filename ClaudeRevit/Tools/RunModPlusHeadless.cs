#if REVIT2027
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ModPlus.Headless;

namespace ClaudeRevit.Tools;

// One compact entry point for every module, in API chat and MCP alike: one schema instead of 85
// copies of the same pipeline schema. Per-module aliases are registered only on request.
public sealed class RunModPlusHeadless : IRevitTool
{
    public string Name => "run_modplus_headless";
    public string Description => "Route a ModPlus headless request to an exact module ID, e.g. mprNumbering. Start with action=describe to inspect its native operation contracts and original method bindings. 32 native API operations across 27 modules need no vendor DLL; typed execute/validate requires the snapshot-matching loaded vendor module and rejects known UI/indirect calls. preview defaults true. Not a full port or an arbitrary-code sandbox. Requires the code-execution switch and the enabled ModPlus group.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresConfirmation => true;
    public bool RequiresCodeExecutionOptIn => true;
    public bool InvalidatesCatalog => true;
    public InputSchema InputSchema
    {
        get
        {
            var schema = ModPlusHeadlessModuleTool.Schema;
            return new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>(schema.Properties!) { ["module"] = NativeToolUtil.Field("string", "Exact module ID from get_modplus_tools, e.g. mprNumbering.") },
                Required = ["module", "action"]
            };
        }
    }
    private static (ModPlusHeadlessModuleTool Tool, Dictionary<string, JsonElement> Args) Route(IReadOnlyDictionary<string, JsonElement> input)
    {
        var module = ToolInput.RequiredString(input, "module");
        var args = input.Where(p => p.Key != "module").ToDictionary(p => p.Key, p => p.Value);
        return (new ModPlusHeadlessModuleTool(module, ModPlusHeadlessRegistration.Bundled), args);
    }
    public void Preflight(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var route = Route(input); route.Tool.Preflight(route.Args, app);
    }
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var route = Route(input); return route.Tool.Execute(route.Args, app);
    }
}
#endif
