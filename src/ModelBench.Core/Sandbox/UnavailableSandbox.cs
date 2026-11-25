using ModelBench.Core.Grading;

namespace ModelBench.Core.Sandbox;

/// <summary>Placeholder wiring until DockerSandbox is configured: any code execution is an infrastructure failure.</summary>
public sealed class UnavailableSandbox : ISandbox
{
    public Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken ct)
        => throw new InfraException("sandbox not configured", retryable: false);
}
