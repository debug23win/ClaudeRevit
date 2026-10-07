namespace ClaudeRevit.Tools;

// Whether a tool may be offered and run, in one place. The rule used to be repeated at eight call
// sites, each spelling it slightly differently.
//
// Code tools (execute_csharp, Python, custom tools...) follow the "allow code execution" setting:
// ON by default, but a user can switch it off, and then neither the chat nor any MCP client
// holding the token can run arbitrary code. They are not subject to the tool-group switches, which
// are a token-saving device. Every other tool follows the group switches.
internal static class ToolPolicy
{
    public static bool IsEnabled(IRevitTool tool, IReadOnlyCollection<string>? disabledGroups = null)
    {
        if (tool.RequiresCodeExecutionOptIn) return Services.SettingsStore.AllowCodeExecution;
        disabledGroups ??= Services.SettingsStore.DisabledToolGroups;
        return disabledGroups.Count == 0 ||
               !disabledGroups.Contains(ToolCatalog.CategoryOf(tool), StringComparer.OrdinalIgnoreCase);
    }

    public const string CodeDisabledMessage =
        "Code execution is turned off in Settings (General → Allow Claude to run code). Use a native tool, " +
        "or ask the user to turn code execution back on.";
}
