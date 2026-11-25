namespace ModelBench.Core.Execution;

public sealed record RetryPolicy(int MaxRetries = 3, int BaseDelayMs = 1000, int MaxDelayMs = 30000)
{
    /// <summary>Exponential backoff: base * 2^retryIndex, capped.</summary>
    public TimeSpan Delay(int retryIndex)
        => TimeSpan.FromMilliseconds(Math.Min(MaxDelayMs, BaseDelayMs * Math.Pow(2, retryIndex)));
}
