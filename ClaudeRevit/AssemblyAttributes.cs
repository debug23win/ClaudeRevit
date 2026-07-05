using System.Runtime.CompilerServices;

// Dynamic/override tools are compiled at runtime into an assembly named
// "ClaudeRevitDynamicTools" (see DynamicToolLoader). Granting it access to this assembly's
// internal types lets an ejected built-in tool — and any custom tool — reuse the same
// internal helpers the built-ins use (ToolInput, TextUtil, FamilyEditorUtil, Services.Json).
// This is not a security boundary: dynamic tools already run arbitrary Revit API code and
// are gated by the code-execution opt-in.
[assembly: InternalsVisibleTo("ClaudeRevitDynamicTools")]
