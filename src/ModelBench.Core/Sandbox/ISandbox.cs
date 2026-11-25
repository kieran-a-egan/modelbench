using System.Text.Json;

namespace ModelBench.Core.Sandbox;

public enum SandboxLanguage { Python, CSharp }

/// <summary>
/// Run = compile (if needed) then call EntryPoint once per input, Repetitions times each (timing = min).
/// Compile = compile only. Inspect = return AST facts only.
/// </summary>
public enum SandboxAction { Run, Compile, Inspect }

/// <summary>
/// What goes INTO the container. By construction it can carry only candidate code and test inputs:
/// never expected outputs, reference solutions or grader code (ADR 0002).
/// </summary>
public sealed record SandboxRequest(
    SandboxLanguage Language,
    SandboxAction Action,
    string Code,
    /// <summary>Function name (Python) or "Class.Method" static method (C#).</summary>
    string EntryPoint,
    /// <summary>Each element is a JSON array of positional arguments.</summary>
    IReadOnlyList<JsonElement> Inputs,
    int TimeoutMs = 10_000,
    int Repetitions = 1);

public sealed record CaseResult(int Index, JsonElement? Output, string? Error, double ElapsedMs);

public sealed record SandboxResult(
    bool Compiled,
    string? CompileErrors,
    IReadOnlyList<CaseResult> Cases,
    /// <summary>Inspect only: imports, defined functions/classes, called names, loop/recursion flags.</summary>
    JsonElement? AstFacts,
    bool TimedOut,
    int ExitCode,
    string StdErrTail);

/// <summary>Executes untrusted candidate code in an isolated, network-less, resource-limited container.</summary>
public interface ISandbox
{
    /// <summary>Throws InfraException if the sandbox could not be started (docker missing, image missing).
    /// Candidate misbehaviour (crash, timeout, OOM) is reported in the result, not thrown.</summary>
    Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken ct);
}
