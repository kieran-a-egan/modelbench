using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Support;

/// <summary>Records every request, then delegates to the inner sandbox.</summary>
internal sealed class RecordingSandbox(ISandbox inner) : ISandbox
{
    public List<SandboxRequest> Requests { get; } = [];

    public Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return inner.ExecuteAsync(request, ct);
    }
}

/// <summary>Scripted sandbox for grader tests that need a specific result (timeout, crash, slow run).</summary>
internal sealed class FakeSandbox(Func<SandboxRequest, Task<SandboxResult>> respond) : ISandbox
{
    public List<SandboxRequest> Requests { get; } = [];

    public Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return respond(request);
    }

    public static SandboxResult TimedOut => new(false, null, [], null, true, 137, "");
    public static SandboxResult Crashed => new(false, null, [], null, false, 1, "Traceback: boom");
}
