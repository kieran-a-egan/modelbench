using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Providers;

/// <summary>Shared HTTP/SSE plumbing for the adapters: one error mapping (ADR 0007) and small JSON helpers.</summary>
internal static class ProviderHttp
{
    public static HttpClient NewClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string BaseUrl(ResolvedModel model) => (model.BaseUrl ?? throw new InvalidOperationException($"{model.Ref} has no baseUrl")).TrimEnd('/');

    public static HttpRequestMessage Post(string url, string body, ResolvedModel model)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in model.Headers) msg.Headers.TryAddWithoutValidation(k, v);
        return msg;
    }

    /// <summary>Sends, maps failures (408/429/5xx and transport = retryable; other non-2xx = not) and hands the stream to <paramref name="parse"/>.</summary>
    public static async Task<ModelResponse> SendAsync(HttpClient http, HttpRequestMessage msg, string body,
        Func<Stream, string, DateTimeOffset, Stopwatch, Task<ModelResponse>> parse, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                var code = (int)resp.StatusCode;
                throw new InfraException($"HTTP {code}: {(err.Length > 500 ? err[..500] : err)}", retryable: code is 408 or 429 or >= 500);
            }
            return await parse(await resp.Content.ReadAsStreamAsync(ct), body, started, sw);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new InfraException($"transport error: {ex.Message}", retryable: true, inner: ex);
        }
    }

    public static JsonDocument ParseChunk(string data)
    {
        try { return JsonDocument.Parse(data); }
        catch (JsonException ex) { throw new InfraException($"malformed stream chunk: {ex.Message}", retryable: false, inner: ex); }
    }

    public static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    public static JsonElement Prop(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (k, v) in source) target[k] = v?.DeepClone();
    }

    /// <summary>Parses tool-call arguments into a JSON object ("{}" when empty).</summary>
    public static JsonNode ArgsObject(string argumentsJson)
        => string.IsNullOrWhiteSpace(argumentsJson) ? new JsonObject() : JsonNode.Parse(argumentsJson) ?? new JsonObject();

    public static JsonNode Schema(ToolDefinition t) => JsonNode.Parse(t.ParametersSchema.GetRawText())!;

    public static JsonArray Tools(ModelRequest request, Func<ToolDefinition, JsonNode> map) => new(request.Tools.Select(map).ToArray());
}
