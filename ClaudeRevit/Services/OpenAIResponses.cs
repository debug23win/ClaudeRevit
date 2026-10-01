using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Stateless Responses transport for the official OpenAI provider. Other compatible
// providers retain Chat Completions. Completed output is parsed atomically: a truncated
// stream must never cause a partially generated Revit tool call to execute.
public static class OpenAIResponses
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static bool UsesResponses(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    public static JsonObject BuildRequest(string model, string systemPrompt,
        IReadOnlyList<ApiTurn> history, string dynamicContext, JsonArray chatTools, bool stream = true)
    {
        var input = new JsonArray();
        foreach (var turn in history)
        {
            foreach (var block in turn.Blocks)
            {
                switch (block)
                {
                    case ChatOpenAIReasoningBlock r when r.Model == model:
                        input.Add(JsonNode.Parse(r.ItemJson));
                        break;
                    case ChatTextBlock t when t.Text.Length > 0:
                        input.Add(new JsonObject { ["role"] = turn.Role, ["content"] = t.Text });
                        break;
                    case ChatToolUseBlock t:
                        input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = t.Id,
                            ["name"] = t.Name, ["arguments"] = t.InputJson });
                        break;
                    case ChatToolResultBlock t:
                        input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = t.ToolUseId,
                            ["output"] = (t.IsError ? "ERROR: " : "") + t.Content });
                        break;
                    case ChatImageBlock im:
                        input.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray {
                            new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{im.MediaType};base64,{im.Base64}" }
                        }});
                        break;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(dynamicContext))
            input.Add(new JsonObject { ["role"] = "user", ["content"] = dynamicContext });
        var tools = new JsonArray();
        foreach (var tool in chatTools)
        {
            var fn = (JsonObject)tool!["function"]!.DeepClone();
            fn["type"] = "function";
            fn["strict"] = false; // Existing Revit schemas deliberately have optional parameters.
            tools.Add(fn);
        }
        return new JsonObject { ["model"] = model, ["instructions"] = systemPrompt,
            ["input"] = input, ["tools"] = tools, ["stream"] = stream,
            ["store"] = false, ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["max_output_tokens"] = 16384 };
    }

    public static async Task<BackendTurn> SendAsync(string baseUrl, string? key, JsonObject body,
        Func<string, Task> onTextDelta, CancellationToken ct, HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Enter an OpenAI API key in Settings → Models. ChatGPT subscriptions do not supply an API key.");
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/responses");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await (client ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            string message;
            try { message = JsonNode.Parse(error)?["error"]?["message"]?.GetValue<string>() ?? "Request rejected"; }
            catch { message = "Request rejected"; }
            throw new InvalidOperationException($"OpenAI {(int)response.StatusCode}: {TextUtil.Truncate(message, 600)}");
        }
        var model = body["model"]!.GetValue<string>();
        if (body["stream"]?.GetValue<bool>() != true)
            return ParseCompleted(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!, model);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                var evt = JsonNode.Parse(data.ToString())!;
                data.Clear();
                var type = evt["type"]?.GetValue<string>();
                if (type is "response.output_text.delta" or "response.refusal.delta")
                    await onTextDelta(evt["delta"]?.GetValue<string>() ?? "");
                else if (type == "response.completed")
                    return ParseCompleted(evt["response"]!, model);
                else if (type is "response.failed" or "response.incomplete" or "error")
                    throw new InvalidOperationException("OpenAI response did not complete. No generated tool calls were executed.");
            }
        }
        throw new IOException("OpenAI stream ended before response.completed. No generated tool calls were executed.");
    }

    public static BackendTurn ParseCompleted(JsonNode response, string model)
    {
        if (response["status"]?.GetValue<string>() != "completed")
            throw new InvalidOperationException("OpenAI response did not complete.");
        var turn = new BackendTurn();
        foreach (var item in response["output"]!.AsArray())
        {
            switch (item?["type"]?.GetValue<string>())
            {
                case "reasoning":
                    turn.Blocks.Add(new ChatOpenAIReasoningBlock(model, item.ToJsonString()));
                    break;
                case "function_call":
                    var args = item["arguments"]!.GetValue<string>();
                    if (JsonNode.Parse(args) is not JsonObject)
                        throw new InvalidOperationException("OpenAI returned invalid tool arguments.");
                    turn.Blocks.Add(new ChatToolUseBlock(item["call_id"]!.GetValue<string>(),
                        item["name"]!.GetValue<string>(), args));
                    break;
                case "message":
                    foreach (var part in item["content"]!.AsArray())
                    {
                        var text = part?["type"]?.GetValue<string>() switch {
                            "output_text" => part["text"]?.GetValue<string>(),
                            "refusal" => part["refusal"]?.GetValue<string>(), _ => null };
                        if (!string.IsNullOrEmpty(text)) turn.Blocks.Add(new ChatTextBlock(text));
                    }
                    break;
            }
        }
        var usage = response["usage"];
        var total = usage?["input_tokens"]?.GetValue<long>() ?? 0;
        turn.CacheReadTokens = usage?["input_tokens_details"]?["cached_tokens"]?.GetValue<long>() ?? 0;
        turn.InputTokens = Math.Max(0, total - turn.CacheReadTokens);
        turn.OutputTokens = usage?["output_tokens"]?.GetValue<long>() ?? 0;
        return turn;
    }
}
