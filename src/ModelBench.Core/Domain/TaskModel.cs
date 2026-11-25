using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelBench.Core.Domain;

/// <summary>How the final answer is extracted from the model's final text. Declared to the model in the prompt.</summary>
public enum AnswerFormat
{
    /// <summary>Content of the last &lt;answer&gt;...&lt;/answer&gt; tag.</summary>
    Tagged,
    /// <summary>Content of the last fenced code block.</summary>
    CodeBlock,
    /// <summary>Tagged or fenced JSON; must parse.</summary>
    Json,
    /// <summary>Whole final text, trimmed.</summary>
    FreeText,
}

/// <summary>Everything visible to the candidate model. Nothing else may be sent.</summary>
public sealed record TaskPrompt(
    string? System,
    string User,
    AnswerFormat AnswerFormat,
    IReadOnlyList<ToolDefinition> Tools)
{
    public static TaskPrompt Simple(string user, AnswerFormat fmt = AnswerFormat.Tagged, string? system = null)
        => new(system, user, fmt, []);
}

public sealed record ToolDefinition(string Name, string Description, JsonElement ParametersSchema);

/// <summary>
/// Grading data. Never leaves the host process except into the DB.
/// Environment: initial simulated-environment state for ToolAssisted/Agentic tasks (family-defined shape).
/// Reference: generator metadata (e.g. reference solution) for diagnostics and selftest.
/// </summary>
public sealed record TaskHidden(
    IReadOnlyList<GraderSpec> Graders,
    JsonElement? Environment = null,
    JsonElement? Reference = null);

/// <summary>One grader invocation. Every assertion it produces inherits Weight and Required.</summary>
public sealed record GraderSpec(string Type, string Name, JsonElement Config, double Weight = 1, bool Required = true);

public enum Role { System, User, Assistant, Tool }

public sealed record ChatMessage(
    Role Role,
    string? Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Null {typeof(T).Name}");

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
