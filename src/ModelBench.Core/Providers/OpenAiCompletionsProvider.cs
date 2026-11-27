using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Providers;

/// <summary>OpenAI-compatible streaming /chat/completions adapter.</summary>
public sealed class OpenAiCompletionsProvider(ResolvedModel model, HttpClient? http = null) : IModelProvider
{
    private readonly HttpClient _http = http ?? ProviderHttp.NewClient();

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var body = BuildBody(request).ToJsonString();
        var msg = ProviderHttp.Post(ProviderHttp.BaseUrl(model) + "/chat/completions", body, model);
        if (model.ApiKey is { Length: > 0 } key && key != "none") msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return ProviderHttp.SendAsync(_http, msg, body, (s, b, started, sw) => ParseAsync(s, b, started, sw, ct), ct);
    }

    private static async Task<ModelResponse> ParseAsync(Stream stream, string rawRequest, DateTimeOffset started, Stopwatch sw, CancellationToken ct)
    {
        StringBuilder text = new(), reasoning = new(), raw = new();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? finish = null;
        TimeSpan? ttft = null;
        long? input = null, output = null, reasoningTokens = null;
        var sawContent = false;

        var sawData = false;
        var ignored = new List<string>();

        void Handle(string data)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException ex) { throw new InfraException($"malformed stream chunk: {ex.Message}", retryable: false, inner: ex); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("error", out var err) && err.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    throw new InfraException($"server error: {Str(err, "message") ?? (err.ValueKind == JsonValueKind.String ? err.GetString() : err.GetRawText())}", retryable: true);
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    input = Long(u, "prompt_tokens");
                    output = Long(u, "completion_tokens");
                    reasoningTokens = u.TryGetProperty("completion_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object ? Long(d, "reasoning_tokens") : null;
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object) return;
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
                // "message" is the non-streaming shape (a server that ignored stream:true).
                if (!(choice.TryGetProperty("delta", out var delta) || choice.TryGetProperty("message", out delta)) || delta.ValueKind != JsonValueKind.Object) return;

                var sawToken = false;
                if (Str(delta, "content") is { Length: > 0 } c) { text.Append(c); sawToken = true; }
                if ((Str(delta, "reasoning_content") ?? Str(delta, "reasoning")) is { Length: > 0 } r) { reasoning.Append(r); sawToken = true; }
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    var pos = 0;
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var at = pos++;
                        if (tc.ValueKind != JsonValueKind.Object) continue;
                        sawToken = true;
                        var idx = Long(tc, "index") is { } n ? (int)Math.Clamp(n, 0, int.MaxValue) : at;
                        if (!calls.TryGetValue(idx, out var acc)) calls[idx] = acc = ("", "", new StringBuilder());
                        var fn = tc.TryGetProperty("function", out var f) ? f : default;
                        calls[idx] = (Str(tc, "id") ?? acc.Id, (fn.ValueKind == JsonValueKind.Object ? Str(fn, "name") : null) ?? acc.Name, acc.Args);
                        if (fn.ValueKind == JsonValueKind.Object && Str(fn, "arguments") is { } a) acc.Args.Append(a);
                    }
                }
                if (sawToken) { ttft ??= sw.Elapsed; sawContent = true; }
            }
        }

        await foreach (var data in SseReader.ReadDataAsync(stream, ct, ignored))
        {
            raw.AppendLine(data);
            if (data.Trim() == "[DONE]") break;
            sawData = true;
            Handle(data);
        }
        if (!sawData && string.Join("\n", ignored).Trim() is { Length: > 0 } body)
        {
            raw.AppendLine(body);
            Handle(body);
        }
        // Some local servers omit finish_reason but still send content; only an empty, unfinished stream is a failure.
        if (finish is null && !sawContent)
            throw new InfraException("stream ended without a completion", retryable: true);

        var toolCalls = calls.Values.Select(c => new ToolCall(c.Id, c.Name, c.Args.ToString())).ToList();
        return new ModelResponse(text.Length > 0 ? text.ToString() : null, toolCalls, reasoning.Length > 0 ? reasoning.ToString() : null,
            finish, new Usage(input, output, reasoningTokens), new Timing(started, ttft, sw.Elapsed), rawRequest, raw.ToString());
    }

    private static string? Str(JsonElement e, string name) => ProviderHttp.Str(e, name);

    private static long? Long(JsonElement e, string name) => ProviderHttp.Long(e, name);

    private JsonObject BuildBody(ModelRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = model.ModelId,
            ["messages"] = new JsonArray(request.Messages.Select(MapMessage).ToArray()),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (request.Tools.Count > 0)
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.ParametersSchema.GetRawText()) },
            }).ToArray());
        if (model.MaxTokens is { } max)
            body[model.Compat["maxTokensField"]?.GetValue<string>() ?? "max_completion_tokens"] = max;
        Merge(body, model.Sampling);
        if (model.Compat["supportsReasoningEffort"]?.GetValue<bool>() == true
            && model.VariantSettings["reasoningEffort"]?.GetValue<string>() is { } effort
            && !(effort == "none" && !model.Reasoning))
            body["reasoning_effort"] = effort;
        Merge(body, model.Extra);
        return body;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (k, v) in source) target[k] = v?.DeepClone();
    }

    private static JsonNode MapMessage(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        if (m.Role == Role.Tool) o["tool_call_id"] = m.ToolCallId;
        return o;
    }
}
