using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeRevit.UI;

public class ChatMessage : INotifyPropertyChanged
{
    private string _text = "";
    private bool _isError;

    public string Role { get; init; } = "user";
    public string? ToolName { get; init; }

    // The label shown for assistant messages. Points at whatever model is actually
    // answering — "Claude" on the Anthropic path, or the alt model id (e.g. "grok-4.3")
    // when an alternative provider is selected — so the pane never mislabels a Grok/Gemini
    // reply as "Claude". Set by the chat pane when the model picker changes.
    public static string AssistantLabel = "Claude";

    public string RoleDisplay => Role switch
    {
        "user" => "You",
        "assistant" => AssistantLabel,
        "tool" => $"🔧 {ToolName}",
        "diag" => "⏱ diagnostics",
        _ => Role
    };

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnPropertyChanged();
        }
    }

    public bool IsError
    {
        get => _isError;
        set
        {
            if (_isError == value) return;
            _isError = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
