using System.Text.Json.Nodes;

namespace ModelBench.Core.Config;

/// <summary>pi-compatible api ids: "openai-completions", "openai-responses", "anthropic-messages", "google-generative-ai", "manual".</summary>
public enum ApiKind { OpenAiCompletions, OpenAiResponses, AnthropicMessages, GoogleGenerativeAi, Manual }

/// <summary>USD per 1M tokens. Reasoning null = billed as output.</summary>
public sealed record Pricing(decimal Input, decimal Output, decimal? Reasoning = null);

/// <summary>Local energy estimate profile: average draw while generating, and electricity price.</summary>
public sealed record EnergyProfile(double Watts, decimal PricePerKwh);

/// <summary>
/// A fully resolved "provider/modelId[@variant]" from models.json.
/// Settings merge order (later wins): model.samplingParams, model.extra, variant settings.
/// </summary>
public sealed record ResolvedModel(
    string Ref,
    string ProviderId,
    ApiKind Api,
    string? BaseUrl,
    string ModelId,
    string? DisplayName,
    /// <summary>Resolved secret. NEVER persisted, logged or exported; excluded from snapshots.</summary>
    string? ApiKey,
    IReadOnlyDictionary<string, string> Headers,
    bool Reasoning,
    bool ToolCalls,
    int? ContextWindow,
    int? MaxTokens,
    Pricing? Pricing,
    EnergyProfile? Energy,
    string? Variant,
    /// <summary>samplingParams (temperature, top_p, seed, ...).</summary>
    JsonObject Sampling,
    /// <summary>Variant settings, e.g. {"reasoningEffort":"low"} or {"thinkingBudget":8192}.</summary>
    JsonObject VariantSettings,
    /// <summary>Wire-compat flags, e.g. maxTokensField, supportsReasoningEffort.</summary>
    JsonObject Compat,
    /// <summary>Raw fields merged into the request body last.</summary>
    JsonObject Extra);
