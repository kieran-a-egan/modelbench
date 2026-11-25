using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Providers;

/// <summary>
/// Gemini streamGenerateContent adapter. Output tokens = candidatesTokenCount + thoughtsTokenCount (Gemini reports them
/// separately; OutputTokens includes reasoning everywhere, see Usage).
/// </summary>
public sealed class GoogleGenerativeAiProvider(ResolvedModel model, HttpClient? http = null) : IModelProvider
{
    private readonly HttpClient _http = http ?? ProviderHttp.NewClient();

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var body = BuildBody(request).ToJsonString();
        var msg = ProviderHttp.Post($"{ProviderHttp.BaseUrl(model)}/models/{model.ModelId}:streamGenerateContent?alt=sse", body, model);
        if (model.ApiKey is { Length: > 0 } key) msg.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        return ProviderHttp.SendAsync(_http, msg, body, (s, b, started, sw) => ParseAsync(s, b, started, sw, ct), ct);
    }

    private static async Task<ModelResponse> ParseAsync(Stream stream, string rawRequest, DateTimeOffset started, Stopwatch sw, CancellationToken ct)
    {
        StringBuilder text = new(), reasoning = new(), raw = new();
        var calls = new List<ToolCall>();
        var parts = new JsonArray();
        string? finish = null;
        TimeSpan? ttft = null;
        long? input = null, candidates = null, thoughts = null;

        await foreach (var data in SseReader.ReadDataAsync(stream, ct))
        {
            raw.Append(data).Append('\n');
            using var doc = ProviderHttp.ParseChunk(data);
            var e = doc.RootElement;
            var u = ProviderHttp.Prop(e, "usageMetadata");
            if (u.ValueKind == JsonValueKind.Object)
            {
                input = ProviderHttp.Long(u, "promptTokenCount") ?? input;
                candidates = ProviderHttp.Long(u, "candidatesTokenCount") ?? candidates;
                thoughts = ProviderHttp.Long(u, "thoughtsTokenCount") ?? thoughts;
            }
            if (ProviderHttp.Prop(e, "candidates") is not { ValueKind: JsonValueKind.Array } cs || cs.GetArrayLength() == 0) continue;
            finish = ProviderHttp.Str(cs[0], "finishReason") ?? finish;
            if (ProviderHttp.Prop(ProviderHttp.Prop(cs[0], "content"), "parts") is not { ValueKind: JsonValueKind.Array } ps) continue;
            foreach (var p in ps.EnumerateArray())
            {
                parts.Add(JsonNode.Parse(p.GetRawText()));
                if (p.TryGetProperty("functionCall", out var fc))
                {
                    calls.Add(new ToolCall($"call_{calls.Count}", ProviderHttp.Str(fc, "name") ?? "", fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}"));
                    ttft ??= sw.Elapsed;
                }
                else if (ProviderHttp.Str(p, "text") is { } t)
                {
                    (p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True ? reasoning : text).Append(t);
                    if (t.Length > 0) ttft ??= sw.Elapsed;
                }
            }
        }

        var output = candidates is null && thoughts is null ? (long?)null : (candidates ?? 0) + (thoughts ?? 0);
        return new ModelResponse(text.Length > 0 ? text.ToString() : null, calls, reasoning.Length > 0 ? reasoning.ToString() : null, finish,
            new Usage(input, output, thoughts), new Timing(started, ttft, sw.Elapsed), rawRequest, raw.ToString(),
            parts.Count > 0 ? parts.ToJsonString() : null);
    }

    private JsonObject BuildBody(ModelRequest request)
    {
        var contents = new JsonArray();
        JsonArray? results = null; // functionResponse parts of the current model turn
        foreach (var m in request.Messages.Where(m => m.Role != Role.System))
        {
            if (m.Role == Role.Tool)
            {
                if (results is null) contents.Add(new JsonObject { ["role"] = "user", ["parts"] = results = [] });
                results.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = m.ToolName, ["response"] = new JsonObject { ["result"] = Result(m.Content) } } });
                continue;
            }
            results = null;
            contents.Add(new JsonObject { ["role"] = m.Role == Role.Assistant ? "model" : "user", ["parts"] = m.Role == Role.Assistant ? ModelParts(m) : new JsonArray(new JsonObject { ["text"] = m.Content }) });
        }

        var body = new JsonObject { ["contents"] = contents };
        if (string.Join("\n\n", request.Messages.Where(m => m.Role == Role.System).Select(m => m.Content)) is { Length: > 0 } system)
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };
        if (request.Tools.Count > 0)
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = ProviderHttp.Tools(request, t => new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = ProviderHttp.Schema(t) }),
            });

        var config = new JsonObject();
        if (model.MaxTokens is { } max) config["maxOutputTokens"] = max;
        ProviderHttp.Merge(config, model.Sampling);
        var thinking = new JsonObject();
        if (model.VariantSettings["thinkingBudget"] is { } budget) thinking["thinkingBudget"] = budget.DeepClone();
        else if ((model.VariantSettings["thinkingLevel"] ?? model.VariantSettings["reasoningEffort"]) is { } level) thinking["thinkingLevel"] = level.DeepClone();
        if (model.Reasoning) thinking["includeThoughts"] = true;
        if (thinking.Count > 0) config["thinkingConfig"] = thinking;
        if (config.Count > 0) body["generationConfig"] = config;
        ProviderHttp.Merge(body, model.Extra);
        return body;
    }

    private static JsonNode? Result(string? content)
    {
        try { return JsonNode.Parse(content ?? "") is { } n && n is JsonObject or JsonArray ? n : content; }
        catch (JsonException) { return content; }
    }

    private static JsonNode ModelParts(ChatMessage m)
    {
        if (m.ProviderState is not null) return JsonNode.Parse(m.ProviderState)!;
        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(m.Content)) parts.Add(new JsonObject { ["text"] = m.Content });
        foreach (var c in m.ToolCalls ?? [])
            parts.Add(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = ProviderHttp.ArgsObject(c.ArgumentsJson) } });
        return parts;
    }
}
