using ModelBench.Core.Domain;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>Output of a generator: the visible prompt, the hidden grading data, and the reference behaviour.</summary>
public sealed record GeneratedTask(
    TaskPrompt Prompt,
    TaskHidden Hidden,
    /// <summary>
    /// Assistant turns a perfect candidate would produce, replayed by ScriptedProvider in selftest.
    /// Single-turn: one turn with the reference answer text. Tool/agentic: tool-call turns then a final text turn.
    /// </summary>
    IReadOnlyList<ChatMessage> ReferenceTurns,
    /// <summary>Plausible wrong behaviours that MUST fail at least one required assertion (selftest).</summary>
    IReadOnlyList<IReadOnlyList<ChatMessage>> KnownBadTurns);

public sealed record ValidationResult(bool Ok, IReadOnlyList<string> Problems)
{
    public static readonly ValidationResult Valid = new(true, []);
    public static ValidationResult Fail(params string[] problems) => new(false, problems);
}

/// <summary>Complexity is a shared 1..5 difficulty scale; each family maps it to its own parameters.</summary>
public static class Complexity
{
    public const int Min = 1, Max = 5;
}

/// <summary>
/// A seeded procedural benchmark family. Implementations must be pure functions of (rng, complexity):
/// no clock, no System.Random, no I/O, no dictionary-order dependence.
/// Bump Version whenever generated output for a given seed would change.
/// </summary>
public interface IBenchmarkFamily
{
    string Id { get; }
    int Version { get; }
    string Category { get; }
    string Description { get; }
    /// <summary>The execution mode these tasks require. SingleTurn tasks can also run in Manual mode.</summary>
    ExecutionMode Mode { get; }

    GeneratedTask Generate(Rng rng, int complexity);

    /// <summary>
    /// Generate for the task at position <paramref name="index"/> within its suite-spec entry (0-based).
    /// Procedural families ignore the index. File-defined families (static:/repo:) use it to yield their tasks in order.
    /// SuiteGenerator and SelfTest always call this overload.
    /// </summary>
    GeneratedTask Generate(Rng rng, int complexity, int index) => Generate(rng, complexity);

    /// <summary>
    /// Independent check of a generated task (e.g. brute-force re-solve, uniqueness, satisfiability).
    /// Must not simply call the same code path that produced the expected answer.
    /// </summary>
    ValidationResult Validate(GeneratedTask task);

    /// <summary>Simulated environment for ToolAssisted/Agentic tasks; null for single-turn families.</summary>
    ISimulatedEnvironment? CreateEnvironment(TaskHidden hidden) => null;

    /// <summary>How many distinct visible prompts the family can produce at this complexity, when that is a small fixed number; null when unbounded for practical purposes.</summary>
    int? Capacity(int complexity) => null;
}

/// <summary>
/// Deterministic in-process tool environment. Tools are simulations only: no real I/O, network or clock.
/// </summary>
public interface ISimulatedEnvironment
{
    /// <summary>Executes a tool call and returns the tool result content (usually JSON). Never throws for bad
    /// arguments: returns an error payload the model can see.</summary>
    string Execute(ToolCall call);

    /// <summary>Current environment state, graded by the simulated-state grader.</summary>
    System.Text.Json.JsonElement State { get; }
}
