using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeRevit.Tools;

namespace ClaudeRevit.Services;

// Chat backend for any OpenAI-compatible /chat/completions endpoint. One implementation
// covers DeepSeek, Qwen (DashScope), OpenRouter, Groq and local Ollama / LM Studio — they
// all speak the same protocol. Configured in Settings (base URL + model id + key); the
// conversation history is the same provider-agnostic ApiTurn model the Anthropic backend
// uses, so the user can switch models mid-conversation.
public sealed class OpenAIBackend
{
    // Infinite: HttpClient.Timeout would abort long SSE streams (reasoning models think
    // for minutes). Cancellation comes from the caller's token (the Cancel button).
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const int MaxOutputTokens = 8192;

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SettingsStore.AltBaseUrl) &&
        !string.IsNullOrWhiteSpace(SettingsStore.AltModel);

    // toolsJson comes prebuilt (see BuildTools) so the ~130-tool schema array is not
    // re-serialized on every agentic-loop iteration; DeepClone because a JsonNode can
    // only have one parent.
    public async Task<BackendTurn> StreamTurnAsync(
        string systemPrompt,
        IReadOnlyList<ApiTurn> history,
        string dynamicContext,
        JsonArray toolsJson,
        Func<string, Task> onTextDelta,
        CancellationToken ct,
        string? modelOverride = null)
    {
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(modelOverride) ? SettingsStore.AltModel : modelOverride,
            ["messages"] = BuildMessages(systemPrompt, history, dynamicContext),
            ["stream"] = true,
            // Most compatible providers honor this and report usage in a final chunk;
            // ones that don't just ignore it (we then estimate the tokens below).
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
        if (toolsJson.Count > 0)
            body["tools"] = toolsJson.DeepClone();

        var (resp, requestChars) = await SendAsync(body, ct);
        using var _ = resp;

        var turn = new BackendTurn();
        var text = new StringBuilder();
        var calls = new List<ToolCallAccumulator>();
        bool gotUsage = false;

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            ThrowOnStreamError(root);

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                ReadUsage(usage, turn);
                gotUsage = true;
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                continue;
            if (!choices[0].TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object)
                continue;

            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var piece = c.GetString()!;
                if (piece.Length > 0)
                {
                    text.Append(piece);
                    // Awaited: the UI dispatcher provides backpressure so a fast local
                    // provider can't flood Revit's UI thread with queued closures.
                    await onTextDelta(piece);
                }
            }
            // delta.reasoning_content (DeepSeek R1 etc.) is intentionally dropped: it has
            // no signature, so storing it would poison a later replay to the Anthropic API.

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                foreach (var tc in tcs.EnumerateArray())
                    AccumulateToolCall(tc, calls);
        }

        if (!gotUsage)
        {
            // Rough fallback (≈4 chars per token) so accounting works on providers that
            // ignore stream_options. The INPUT estimate matters most: ChatService's
            // history-compaction trigger reads it — leaving it 0 would grow the
            // conversation past the model's context window with no compaction ever.
            turn.OutputTokens = Math.Max(1, text.Length / 4);
            turn.InputTokens = Math.Max(1, requestChars / 4);
        }

        if (text.Length > 0)
            turn.Blocks.Add(new ChatTextBlock(text.ToString()));
        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var args = call.Arguments.Length > 0 ? call.Arguments.ToString() : "{}";
            var id = string.IsNullOrEmpty(call.Id) ? $"call_{Guid.NewGuid():N}" : call.Id;
            if (string.IsNullOrEmpty(call.Name)) continue; // malformed fragment — nothing to execute
            turn.Blocks.Add(new ChatToolUseBlock(id, call.Name, args));
        }
        return turn;
    }

    // Single non-streamed completion with no tools — used for history compaction.
    public async Task<string?> CompleteOnceAsync(string userContent, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = SettingsStore.AltModel,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userContent }
            },
            ["stream"] = false
        };
        var (resp, _) = await SendAsync(body, ct, maxTokens: 2000);
        using var __ = resp;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        ThrowOnStreamError(root);
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString();
        return null;
    }

    // Endpoints that rejected max_tokens once (gpt-5 family, o-series want
    // max_completion_tokens) — remembered per base URL so every later request uses the
    // right parameter directly instead of paying a failed round trip each call.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        NeedsMaxCompletionTokens = new();

    private static async Task<(HttpResponseMessage Resp, int RequestChars)> SendAsync(
        JsonObject body, CancellationToken ct, int maxTokens = MaxOutputTokens)
    {
        var baseUrl = NormalizeBaseUrl(SettingsStore.AltBaseUrl);
        // The key cannot change mid-turn — read the DPAPI file once per request cycle,
        // not once per retry.
        var key = ApiKeyStore.LoadAlt();

        var limitParam = NeedsMaxCompletionTokens.ContainsKey(baseUrl)
            ? "max_completion_tokens" : "max_tokens";
        body[limitParam] = maxTokens;

        var (resp, error, rawError, chars) = await PostOnceAsync(baseUrl, key, body, ct);
        if (resp != null) return (resp, chars);

        if (limitParam == "max_tokens" && IsMaxTokensRejection(rawError))
        {
            NeedsMaxCompletionTokens[baseUrl] = true;
            body.Remove("max_tokens");
            body["max_completion_tokens"] = maxTokens;
            (resp, error, rawError, chars) = await PostOnceAsync(baseUrl, key, body, ct);
            if (resp != null) return (resp, chars);
        }
        throw new InvalidOperationException(error);
    }

    // OpenAI's structured error (code=unsupported_parameter, param=max_tokens) is checked
    // FIRST; the message-text match is the fallback for providers that only mimic the
    // human-readable part.
    private static bool IsMaxTokensRejection(string? rawErrorBody)
    {
        if (string.IsNullOrEmpty(rawErrorBody)) return false;
        try
        {
            using var doc = JsonDocument.Parse(rawErrorBody);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String &&
                p.GetString() == "max_tokens")
                return true;
        }
        catch { /* not JSON — fall through to the text match */ }
        return rawErrorBody.Contains("max_completion_tokens");
    }

    // Users routinely paste a full endpoint URL (or the OpenAI "Responses API" path that
    // xAI/Grok and others don't expose) into the base-URL field. Strip a mistakenly-included
    // endpoint suffix so we still hit /chat/completions instead of 404ing on, e.g.,
    // https://api.x.ai/v1/responses. The base URL should be the root like https://api.x.ai/v1.
    private static string NormalizeBaseUrl(string raw)
    {
        var url = (raw ?? "").Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/responses", "/completions" })
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^suffix.Length].TrimEnd('/');
                break;
            }
        return url;
    }

    private static async Task<(HttpResponseMessage? Resp, string? Error, string? RawError, int RequestChars)>
        PostOnceAsync(string baseUrl, string? key, JsonObject body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        // OpenRouter attribution headers (harmless elsewhere).
        req.Headers.TryAddWithoutValidation("X-Title", "ClaudeRevit");
        var json = body.ToJsonString();
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            req.Dispose();
            throw new InvalidOperationException(
                $"Cannot reach {baseUrl}: {ex.Message}. Check the base URL in Settings " +
                "(for local Ollama/LM Studio make sure the server is running).");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            var formatted =
                $"{(int)resp.StatusCode} from {baseUrl} (model {SettingsStore.AltModel}): " +
                TextUtil.Truncate(ExtractErrorMessage(err), 600);
            resp.Dispose();
            req.Dispose();
            return (null, formatted, err, json.Length);
        }
        return (resp, null, null, json.Length);
    }

    // Providers report errors mid-stream as a data chunk with an "error" object.
    private static void ThrowOnStreamError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object)
            return;
        var msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : err.GetRawText();
        throw new InvalidOperationException($"Provider error: {Truncate(msg ?? "unknown", 600)}");
    }

    private static void ReadUsage(JsonElement usage, BackendTurn turn)
    {
        long prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
        long completion = usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
        // Standard OpenAI shape, then DeepSeek's own field.
        long cached = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var det) &&
            det.ValueKind == JsonValueKind.Object &&
            det.TryGetProperty("cached_tokens", out var ch) && ch.ValueKind == JsonValueKind.Number)
            cached = ch.GetInt64();
        else if (usage.TryGetProperty("prompt_cache_hit_tokens", out var dsh) && dsh.ValueKind == JsonValueKind.Number)
            cached = dsh.GetInt64();

        turn.InputTokens = Math.Max(0, prompt - cached);
        turn.CacheReadTokens = cached;
        turn.OutputTokens = completion;
    }

    private static void AccumulateToolCall(JsonElement tc, List<ToolCallAccumulator> calls)
    {
        // Streaming fragments carry an index; a fresh id also signals a new call for the
        // rare providers that omit the index.
        int idx;
        if (tc.TryGetProperty("index", out var ixEl) && ixEl.ValueKind == JsonValueKind.Number)
            idx = ixEl.GetInt32();
        else if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrEmpty(idEl.GetString()))
            idx = calls.Count;
        else
            idx = Math.Max(0, calls.Count - 1);

        while (calls.Count <= idx) calls.Add(new ToolCallAccumulator());
        var call = calls[idx];

        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(id.GetString()))
            call.Id = id.GetString()!;
        if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
        {
            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                call.Name += name.GetString();
            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                call.Arguments.Append(args.GetString());
        }
    }

    private static JsonArray BuildMessages(
        string systemPrompt, IReadOnlyList<ApiTurn> history, string dynamicContext)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };

        foreach (var turn in history)
        {
            if (turn.Role == "assistant")
            {
                var text = string.Join("\n\n",
                    turn.Blocks.OfType<ChatTextBlock>().Select(b => b.Text).Where(t => t.Length > 0));
                var toolCalls = new JsonArray();
                foreach (var tu in turn.Blocks.OfType<ChatToolUseBlock>())
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = tu.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tu.Name,
                            ["arguments"] = tu.InputJson
                        }
                    });
                }
                // Thinking blocks are Anthropic-specific and are skipped; a turn that was
                // ONLY thinking has nothing to replay.
                if (text.Length == 0 && toolCalls.Count == 0) continue;
                var msg = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = text.Length > 0 ? text : null
                };
                if (toolCalls.Count > 0) msg["tool_calls"] = toolCalls;
                messages.Add(msg);
            }
            else
            {
                // Tool results answer the previous assistant tool_calls and must come first.
                foreach (var tr in turn.Blocks.OfType<ChatToolResultBlock>())
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = tr.ToolUseId,
                        ["content"] = (tr.IsError ? "ERROR: " : "") + tr.Content
                    });
                }
                var text = string.Join("\n\n",
                    turn.Blocks.OfType<ChatTextBlock>().Select(b => b.Text).Where(t => t.Length > 0));
                var images = turn.Blocks.OfType<ChatImageBlock>().ToList();
                if (images.Count > 0)
                {
                    // OpenAI-compatible vision: content becomes an array of text + image_url
                    // parts, images as data URIs.
                    var parts = new JsonArray();
                    if (text.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    foreach (var im in images)
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{im.MediaType};base64,{im.Base64}" }
                        });
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                }
                else if (text.Length > 0)
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            }
        }

        // Same idea as the Anthropic path's trailing uncached block: current document +
        // selection ride along as the final message so they never disturb the history.
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = dynamicContext });
        return messages;
    }

    // Public: ChatService builds this once per user prompt and reuses it across all
    // agentic-loop iterations of the turn.
    public static JsonArray BuildTools(IReadOnlyList<IRevitTool> tools)
    {
        // Providers disagree on no-argument tools: xAI/Grok, OpenAI, DeepSeek, Groq… REQUIRE a
        // "parameters" field on every tool (a missing one is a 422), while Gemini's OpenAI
        // layer REJECTS an object schema with empty properties. So include parameters always,
        // except omit it for a no-arg tool ONLY on Gemini.
        var isGemini = (SettingsStore.AltBaseUrl ?? "")
            .Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);

        var arr = new JsonArray();
        foreach (var t in tools)
        {
            var props = new JsonObject();
            foreach (var kv in t.InputSchema.Properties ?? new Dictionary<string, JsonElement>())
                props[kv.Key] = JsonSerializer.SerializeToNode(kv.Value);
            var required = new JsonArray();
            foreach (var r in t.InputSchema.Required ?? Array.Empty<string>())
                required.Add(r);

            var fn = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description
            };
            if (props.Count > 0 || !isGemini)
                fn["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = required
                };

            arr.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
        }
        return arr;
    }

    private static string ExtractErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString()!;
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString()!;
            }
        }
        catch { /* not JSON — return as-is */ }
        return responseBody;
    }

    private static string Truncate(string s, int max) => TextUtil.Truncate(s, max);

    private sealed class ToolCallAccumulator
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Arguments = new();
    }
}
