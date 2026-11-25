using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Grading;

/// <summary>Everything a grader may look at. Graders are deterministic functions of this context.</summary>
public sealed record GradingContext(
    TaskPrompt Prompt,
    TaskHidden Hidden,
    /// <summary>Final assistant text, with reasoning blocks (&lt;think&gt;) removed.</summary>
    string? FinalText,
    /// <summary>Answer extracted per Prompt.AnswerFormat; null if extraction failed.</summary>
    string? Answer,
    IReadOnlyList<ChatMessage> Transcript,
    /// <summary>Simulated environment state after the episode (ToolAssisted/Agentic), else null.</summary>
    JsonElement? FinalEnvironmentState,
    ISandbox Sandbox,
    CancellationToken CancellationToken);

/// <summary>One checked fact. Passed must be true only when Score == 1.</summary>
public sealed record AssertionOutcome(
    string Name,
    string? Expected,
    string? Actual,
    double Score,
    bool Passed,
    string? Diagnostic = null)
{
    public static AssertionOutcome Check(string name, bool ok, string? expected, string? actual, string? diagnostic = null)
        => new(name, expected, actual, ok ? 1 : 0, ok, ok ? null : diagnostic);
}

/// <summary>
/// A deterministic grader. Never calls an LLM. Throws InfraException (only) when the grading
/// infrastructure itself failed (e.g. sandbox unavailable) so the attempt is recorded as InfraError.
/// </summary>
public interface IGrader
{
    /// <summary>Matches GraderSpec.Type.</summary>
    string Type { get; }
    Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext context);
}

/// <summary>Infrastructure failure (transport, rate limit, 5xx, sandbox unavailable). Not a model failure.</summary>
public class InfraException(string message, bool retryable = true, Exception? inner = null, TimeSpan? retryAfter = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
    /// <summary>Server-requested delay (HTTP Retry-After), honoured by the runner up to RetryPolicy.MaxDelayMs.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
