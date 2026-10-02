namespace ClaudeRevit.Services;

// Independent selections for modeller and judge. No tag from one provider can
// silently change another provider or turn an explicit API run into a subscription run.
public sealed record BenchmarkExecution(string Backend = "codex", string? Model = null, string? Effort = null)
{
    public string Tag => Backend switch
    {
        "codex" => "codex" + (string.IsNullOrWhiteSpace(Model) ? "" : ":" + Model),
        "claudecode" => "claudecode",
        "api" => ApiTag(Model),
        _ => throw new ArgumentException("Unknown benchmark backend: " + Backend)
    };
    public McpAgentSelection? Agent => Backend == "api" ? null : new(Backend, Model, Effort);
    private static string ApiTag(string? model)
    {
        var tag = string.IsNullOrWhiteSpace(model) ? "auto" : model.Trim();
        if (tag.Equals("claudecode", StringComparison.OrdinalIgnoreCase) || tag.Equals("codex", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("codex:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an API model, or change the benchmark backend to its subscription option.");
        return tag;
    }
}
