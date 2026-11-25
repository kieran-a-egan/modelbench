using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Providers;

/// <summary>
/// Anthropic /v1/messages streaming adapter. Thinking variants: effort/reasoningEffort -> output_config.effort,
/// thinking:"adaptive" -> adaptive thinking, thinkingBudget -> enabled+budget_tokens (older models only: current models
/// reject budget_tokens with HTTP 400, surfaced as a non-retryable InfraException).
/// </summary>
public sealed class AnthropicMessagesProvider : IModelProvider
{
    private static readonly string[] RejectedSampling = ["temperature", "top_p", "top_k"];
    private readonly ResolvedModel _model;
    private readonly HttpClient _http;
    private readonly string _version;

    public AnthropicMessagesProvider(ResolvedModel model, HttpClient? http = null)
    {
        _model = model;
        _http = http ?? ProviderHttp.NewClient();
        _version = model.Compat["anthropicVersion"]?.GetValue<string>() ?? "2026-10-21";
        // Date strings compare correctly with ordinal comparison.
        if (string.CompareOrdinal(_version, "2026-10-21") >= 0 && RejectedSampling.FirstOrDefault(k => model.Sampling.ContainsKey(k)) is { } bad)
            throw new InvalidOperationException($"{model.Ref}: anthropic-version {_version} rejects '{bad}' (HTTP 400 on every model); remove temperature/top_p/top_k from samplingParams");
    }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var body = BuildBody(request).ToJsonString();
        var baseUrl = ProviderHttp.BaseUrl(_model);
        if (baseUrl.EndsWith("/v1", StringComparison.Ordinal)) baseUrl = baseUrl[..^3];
        var msg = ProviderHttp.Post(baseUrl + "/v1/messages", body, _model);
        if (_model.ApiKey is { Length: > 0 } key) msg.Headers.TryAddWithoutValidation("x-api-key", key);
        msg.Headers.TryAddWithoutValidation("anthropic-version", _version);
        if (_model.Compat["anthropicBeta"]?.GetValue<string>() is { Length: > 0 } beta) msg.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        return ProviderHttp.SendAsync(_http, msg, body, (s, b, started, sw) => ParseAsync(s, b, started, sw, ct), ct);
    }

    private sealed class Block(string type, JsonObject start)
    {
        public string Type = type;
        public JsonObject Start = start;
        public StringBuilder Text = new(), Signature = new(), Args = new();
    }

    private static async Task<ModelResponse> ParseAsync(Stream stream, string rawRequest, DateTimeOffset started, Stopwatch sw, CancellationToken ct)
    {
        var blocks = new SortedDictionary<int, Block>();
        var raw = new StringBuilder();
        string? finish = null;
        TimeSpan? ttft = null;
        long? input = null, output = null;

        await foreach (var data in SseReader.ReadDataAsync(stream, ct))
        {
            raw.Append(data).Append('\n');
            using var doc = ProviderHttp.ParseChunk(data);
            var e = doc.RootElement;
            var index = (int)(ProviderHttp.Long(e, "index") ?? 0);
            switch (ProviderHttp.Str(e, "type"))
            {
                case "message_start":
                    input = ProviderHttp.Long(ProviderHttp.Prop(ProviderHttp.Prop(e, "message"), "usage"), "input_tokens");
                    break;
                case "content_block_start":
                    var cb = ProviderHttp.Prop(e, "content_block");
                    blocks[index] = new Block(ProviderHttp.Str(cb, "type") ?? "text", (JsonObject)JsonNode.Parse(cb.GetRawText())!);
                    break;
                case "content_block_delta":
                    var d = ProviderHttp.Prop(e, "delta");
                    var b = blocks[index];
                    switch (ProviderHttp.Str(d, "type"))
                    {
                        case "text_delta": b.Text.Append(ProviderHttp.Str(d, "text")); break;
                        case "thinking_delta": b.Text.Append(ProviderHttp.Str(d, "thinking")); break;
                        case "signature_delta": b.Signature.Append(ProviderHttp.Str(d, "signature")); continue;
                        case "input_json_delta": b.Args.Append(ProviderHttp.Str(d, "partial_json")); break;
                    }
                    ttft ??= sw.Elapsed;
                    break;
                case "message_delta":
                    finish = ProviderHttp.Str(ProviderHttp.Prop(e, "delta"), "stop_reason") ?? finish;
                    output = ProviderHttp.Long(ProviderHttp.Prop(e, "usage"), "output_tokens") ?? output;
                    break;
                case "error":
                    var err = ProviderHttp.Prop(e, "error");
                    var kind = ProviderHttp.Str(err, "type");
                    throw new InfraException($"stream error {kind}: {ProviderHttp.Str(err, "message")}",
                        retryable: kind is "overloaded_error" or "api_error" or "rate_limit_error");
            }
        }

        StringBuilder text = new(), reasoning = new();
        var calls = new List<ToolCall>();
        var state = new JsonArray();
        foreach (var b in blocks.Values)
            switch (b.Type)
            {
                case "text":
                    text.Append(b.Text);
                    state.Add(new JsonObject { ["type"] = "text", ["text"] = b.Text.ToString() });
                    break;
                case "thinking":
                    reasoning.Append(b.Text);
                    state.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = b.Text.ToString(), ["signature"] = b.Signature.ToString() });
                    break;
                case "tool_use":
                    var args = b.Args.Length > 0 ? b.Args.ToString() : "{}";
                    calls.Add(new ToolCall((string?)b.Start["id"] ?? "", (string?)b.Start["name"] ?? "", args));
                    state.Add(new JsonObject { ["type"] = "tool_use", ["id"] = (string?)b.Start["id"], ["name"] = (string?)b.Start["name"], ["input"] = ProviderHttp.ArgsObject(args) });
                    break;
                default: // redacted_thinking etc.: replay as received
                    state.Add(b.Start);
                    break;
            }

        return new ModelResponse(text.Length > 0 ? text.ToString() : null, calls, reasoning.Length > 0 ? reasoning.ToString() : null, finish,
            new Usage(input, output, null), new Timing(started, ttft, sw.Elapsed), rawRequest, raw.ToString(),
            state.Count > 0 ? state.ToJsonString() : null);
    }

    private JsonObject BuildBody(ModelRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = _model.ModelId,
            ["max_tokens"] = _model.MaxTokens ?? 16000,
            ["stream"] = true,
        };
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == Role.System).Select(m => m.Content));
        if (system.Length > 0) body["system"] = system;

        var messages = new JsonArray();
        JsonArray? results = null; // tool_result blocks of the current assistant turn
        foreach (var m in request.Messages.Where(m => m.Role != Role.System))
        {
            if (m.Role == Role.Tool)
            {
                if (results is null) messages.Add(new JsonObject { ["role"] = "user", ["content"] = results = [] });
                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content ?? "" });
                continue;
            }
            results = null;
            messages.Add(new JsonObject { ["role"] = m.Role == Role.Assistant ? "assistant" : "user", ["content"] = m.Role == Role.Assistant ? AssistantContent(m) : m.Content });
        }
        body["messages"] = messages;

        if (request.Tools.Count > 0)
            body["tools"] = ProviderHttp.Tools(request, t => new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = ProviderHttp.Schema(t) });

        var v = _model.VariantSettings;
        if ((v["effort"] ?? v["reasoningEffort"])?.GetValue<string>() is { } effort) body["output_config"] = new JsonObject { ["effort"] = effort };
        if (v["thinking"]?.GetValue<string>() == "adaptive") body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
        else if (v["thinkingBudget"] is { } budget) body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget.DeepClone() };

        ProviderHttp.Merge(body, _model.Sampling);
        ProviderHttp.Merge(body, _model.Extra);
        return body;
    }

    private static JsonNode AssistantContent(ChatMessage m)
    {
        if (m.ProviderState is not null) return JsonNode.Parse(m.ProviderState)!;
        var blocks = new JsonArray();
        if (!string.IsNullOrEmpty(m.Content)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
        foreach (var c in m.ToolCalls ?? [])
            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = ProviderHttp.ArgsObject(c.ArgumentsJson) });
        return blocks;
    }
}
