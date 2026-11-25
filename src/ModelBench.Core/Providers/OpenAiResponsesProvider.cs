using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Providers;

/// <summary>OpenAI /responses streaming adapter (stateless: store=false, output items replayed via ProviderState).</summary>
public sealed class OpenAiResponsesProvider(ResolvedModel model, HttpClient? http = null) : IModelProvider
{
    private readonly HttpClient _http = http ?? ProviderHttp.NewClient();

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var body = BuildBody(request).ToJsonString();
        var msg = ProviderHttp.Post(ProviderHttp.BaseUrl(model) + "/responses", body, model);
        if (model.ApiKey is { Length: > 0 } key && key != "none") msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return ProviderHttp.SendAsync(_http, msg, body, (s, b, started, sw) => ParseAsync(s, b, started, sw, ct), ct);
    }

    private static async Task<ModelResponse> ParseAsync(Stream stream, string rawRequest, DateTimeOffset started, Stopwatch sw, CancellationToken ct)
    {
        StringBuilder text = new(), reasoning = new(), raw = new();
        var items = new SortedDictionary<int, JsonNode>();
        JsonNode? completedOutput = null;
        string? finish = null;
        TimeSpan? ttft = null;
        long? input = null, output = null, reasoningTokens = null;

        await foreach (var data in SseReader.ReadDataAsync(stream, ct))
        {
            raw.Append(data).Append('\n');
            if (data.Trim() == "[DONE]") break;
            using var doc = ProviderHttp.ParseChunk(data);
            var e = doc.RootElement;
            switch (ProviderHttp.Str(e, "type"))
            {
                case "response.output_text.delta": text.Append(ProviderHttp.Str(e, "delta")); ttft ??= sw.Elapsed; break;
                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta": reasoning.Append(ProviderHttp.Str(e, "delta")); ttft ??= sw.Elapsed; break;
                case "response.function_call_arguments.delta": ttft ??= sw.Elapsed; break;
                case "response.output_item.done":
                    items[(int)(ProviderHttp.Long(e, "output_index") ?? items.Count)] = JsonNode.Parse(ProviderHttp.Prop(e, "item").GetRawText())!;
                    break;
                case "response.completed" or "response.incomplete":
                    var r = ProviderHttp.Prop(e, "response");
                    var u = ProviderHttp.Prop(r, "usage");
                    input = ProviderHttp.Long(u, "input_tokens");
                    output = ProviderHttp.Long(u, "output_tokens");
                    reasoningTokens = ProviderHttp.Long(ProviderHttp.Prop(u, "output_tokens_details"), "reasoning_tokens");
                    finish = ProviderHttp.Str(ProviderHttp.Prop(r, "incomplete_details"), "reason") ?? ProviderHttp.Str(r, "status");
                    if (ProviderHttp.Prop(r, "output") is { ValueKind: JsonValueKind.Array } o && o.GetArrayLength() > 0) completedOutput = JsonNode.Parse(o.GetRawText());
                    break;
                case "response.failed" or "error":
                    var err = e.TryGetProperty("response", out var resp) ? ProviderHttp.Prop(resp, "error") : e;
                    var code = ProviderHttp.Str(err, "code");
                    throw new InfraException($"stream error {code}: {ProviderHttp.Str(err, "message")}", retryable: code is "server_error" or "rate_limit_exceeded" or "overloaded");
            }
        }

        var output_items = completedOutput as JsonArray ?? new JsonArray(items.Values.Select(i => i.DeepClone()).ToArray());
        var calls = output_items.Where(i => (string?)i!["type"] == "function_call")
            .Select(i => new ToolCall((string?)i!["call_id"] ?? "", (string?)i["name"] ?? "", (string?)i["arguments"] ?? "{}")).ToList();
        return new ModelResponse(text.Length > 0 ? text.ToString() : null, calls, reasoning.Length > 0 ? reasoning.ToString() : null, finish,
            new Usage(input, output, reasoningTokens), new Timing(started, ttft, sw.Elapsed), rawRequest, raw.ToString(),
            output_items.Count > 0 ? output_items.ToJsonString() : null);
    }

    private JsonObject BuildBody(ModelRequest request)
    {
        var input = new JsonArray();
        foreach (var m in request.Messages.Where(m => m.Role != Role.System))
        {
            if (m.Role == Role.Tool)
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = m.ToolCallId, ["output"] = m.Content ?? "" });
            else if (m.Role == Role.User)
                input.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content });
            else if (m.ProviderState is not null)
                foreach (var item in JsonNode.Parse(m.ProviderState)!.AsArray()) input.Add(item!.DeepClone());
            else
            {
                if (!string.IsNullOrEmpty(m.Content)) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Content });
                foreach (var c in m.ToolCalls ?? [])
                    input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = c.Id, ["name"] = c.Name, ["arguments"] = c.ArgumentsJson });
            }
        }

        var body = new JsonObject { ["model"] = model.ModelId, ["stream"] = true, ["store"] = false };
        if (model.Reasoning) body["include"] = new JsonArray("reasoning.encrypted_content");
        if (string.Join("\n\n", request.Messages.Where(m => m.Role == Role.System).Select(m => m.Content)) is { Length: > 0 } system) body["instructions"] = system;
        body["input"] = input;
        if (request.Tools.Count > 0)
            body["tools"] = ProviderHttp.Tools(request, t => new JsonObject { ["type"] = "function", ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = ProviderHttp.Schema(t) });
        if (model.VariantSettings["reasoningEffort"]?.GetValue<string>() is { } effort) body["reasoning"] = new JsonObject { ["effort"] = effort };
        if (model.MaxTokens is { } max) body["max_output_tokens"] = max;
        ProviderHttp.Merge(body, model.Sampling);
        ProviderHttp.Merge(body, model.Extra);
        return body;
    }
}
