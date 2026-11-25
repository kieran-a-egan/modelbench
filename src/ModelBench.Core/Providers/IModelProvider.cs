using ModelBench.Core.Domain;

namespace ModelBench.Core.Providers;

public sealed record ModelRequest(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition> Tools);

/// <summary>
/// Token counts exactly as reported by the provider; null = not reported (never estimated).
/// OutputTokens INCLUDES reasoning tokens (cost is computed on that basis): adapters whose API reports them separately
/// (Gemini: candidatesTokenCount + thoughtsTokenCount) add them. Anthropic reports no separate reasoning count (null).
/// </summary>
public sealed record Usage(long? InputTokens, long? OutputTokens, long? ReasoningTokens)
{
    public static readonly Usage Unknown = new(null, null, null);
}

/// <summary>TimeToFirstToken: first content/reasoning/tool delta in a streamed response; null if not streamed.</summary>
public sealed record Timing(DateTimeOffset Started, TimeSpan? TimeToFirstToken, TimeSpan Total);

public sealed record ModelResponse(
    string? Text,
    IReadOnlyList<ToolCall> ToolCalls,
    string? ReasoningText,
    string? FinishReason,
    Usage Usage,
    Timing Timing,
    /// <summary>Exact request body sent (secrets never appear in bodies; headers are not recorded).</summary>
    string RawRequest,
    /// <summary>Raw response body (or concatenated SSE data lines).</summary>
    string RawResponse,
    /// <summary>Provider-native assistant content to replay verbatim (see ChatMessage.ProviderState); null if the API needs none.</summary>
    string? ProviderState = null);

/// <summary>
/// One adapter per API kind. Throws InfraException for transport errors, HTTP 408/429/5xx (Retryable=true)
/// and other non-2xx (Retryable=false). Never retries internally; the runner owns retry policy.
/// </summary>
public interface IModelProvider
{
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
}
