using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClaudeRevit.Services;

// Central JSON serialization for tool output and its on-screen display. The default
// serializer \uXXXX-escapes every non-ASCII character (so Cyrillic becomes unreadable, and
// costs several tokens each) and also escapes > + & " for HTML safety we don't need. The
// relaxed encoder emits those characters as themselves — readable in the chat pane and
// cheaper in tokens. Safe here: output goes only to the Anthropic/OpenAI JSON APIs and to
// WPF text, never to raw HTML.
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
