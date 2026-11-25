namespace ModelBench.Core.Domain;

public enum ExecutionMode { SingleTurn, ToolAssisted, Agentic, Manual }
public enum RunStatus { Pending, Running, Completed, Cancelled, Failed }
/// <summary>Failed = model failure. InfraError/Timeout/Cancelled are excluded from correctness scores.</summary>
public enum AttemptOutcome { Pending, Passed, Failed, InfraError, Timeout, Cancelled }
public enum Provenance { Measured, Calculated, Estimated, Unavailable }

/// <summary>A frozen, reproducible set of task instances. Generated before any run.</summary>
public class Suite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public long MasterSeed { get; set; }
    /// <summary>Serialized SuiteSpec (families, complexities, counts, generator versions).</summary>
    public required string SpecJson { get; set; }
    /// <summary>SHA-256 over ordered task content hashes.</summary>
    public required string ContentHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TaskInstance> Tasks { get; set; } = [];
}

public class TaskInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SuiteId { get; set; }
    public int Index { get; set; }
    public required string FamilyId { get; set; }
    public int FamilyVersion { get; set; }
    public required string Category { get; set; }
    public int Complexity { get; set; }
    public long Seed { get; set; }
    /// <summary>The mode the task requires (SingleTurn tasks may also be run Manual).</summary>
    public ExecutionMode Mode { get; set; }
    /// <summary>Serialized TaskPrompt. The ONLY task data a provider request may be built from.</summary>
    public required string PromptJson { get; set; }
    /// <summary>Serialized TaskHidden. Never sent to a model or a sandbox.</summary>
    public required string HiddenJson { get; set; }
    public required string ContentHash { get; set; }
}

public class Run
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SuiteId { get; set; }
    public Suite? Suite { get; set; }
    public required string ModelRef { get; set; }
    /// <summary>ResolvedModel snapshot with secrets stripped.</summary>
    public required string ModelSnapshotJson { get; set; }
    public ExecutionMode Mode { get; set; }
    public int Repeats { get; set; } = 1;
    public int Concurrency { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 300;
    public required string RetryPolicyJson { get; set; }
    /// <summary>OS, .NET, docker version, sandbox image digests, app git SHA.</summary>
    public required string EnvironmentJson { get; set; }
    public RunStatus Status { get; set; }
    /// <summary>Set by 'run cancel' (or the web button); the executing process polls it and stops.</summary>
    public bool CancelRequested { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<Attempt> Attempts { get; set; } = [];
}

public class Attempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid TaskId { get; set; }
    public TaskInstance? Task { get; set; }
    public int RepeatIndex { get; set; }
    public AttemptOutcome Outcome { get; set; }
    /// <summary>Weighted mean of assertion scores, 0..1. Null unless graded.</summary>
    public double? Score { get; set; }
    public string? ErrorMessage { get; set; }
    public int Retries { get; set; }
    /// <summary>Raw provider payloads (JSON array, one entry per model call), secrets stripped.</summary>
    public string? RawRequestsJson { get; set; }
    public string? RawResponsesJson { get; set; }
    /// <summary>Serialized normalized transcript (List&lt;ChatMessage&gt;).</summary>
    public string? TranscriptJson { get; set; }
    public string? FinalText { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<AssertionResult> Assertions { get; set; } = [];
    public List<Metric> Metrics { get; set; } = [];
}

public class AssertionResult
{
    public long Id { get; set; }
    public Guid AttemptId { get; set; }
    public required string Grader { get; set; }
    public required string Name { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public double Score { get; set; }
    public double Weight { get; set; } = 1;
    public bool Required { get; set; }
    public bool Passed { get; set; }
    public string? Diagnostic { get; set; }
}

public class Metric
{
    public long Id { get; set; }
    public Guid AttemptId { get; set; }
    public required string Name { get; set; }
    public double? Value { get; set; }
    public required string Unit { get; set; }
    public Provenance Provenance { get; set; }
}

public static class MetricNames
{
    public const string InputTokens = "input_tokens", OutputTokens = "output_tokens", ReasoningTokens = "reasoning_tokens",
        TimeToFirstTokenMs = "ttft_ms", ThroughputTps = "throughput_tps", DurationMs = "duration_ms",
        ModelCalls = "model_calls", CostUsd = "cost_usd", EnergyKwh = "energy_kwh", EnergyCostUsd = "energy_cost_usd";
}
