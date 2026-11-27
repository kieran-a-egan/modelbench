using System.Security.Cryptography;
using System.Text;

namespace ModelBench.Core.Execution;

public sealed record RetryPolicy(int MaxRetries = 3, int BaseDelayMs = 1000, int MaxDelayMs = 30000)
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>Exponential backoff: base * 2^retryIndex, capped.</summary>
    public TimeSpan Delay(int retryIndex)
        => TimeSpan.FromMilliseconds(Math.Min(MaxDelayMs, BaseDelayMs * Math.Pow(2, retryIndex)));

    /// <summary>
    /// Backoff with "equal jitter" (50-100% of the backoff), deterministic per (run, task, repeat, retry) so tests are stable.
    /// A server-provided Retry-After is honoured when longer, even above MaxDelayMs, up to 10 minutes. Otherwise the result never exceeds MaxDelayMs.
    /// </summary>
    public TimeSpan Delay(int retryIndex, Guid runId, Guid taskId, int repeat, TimeSpan? retryAfter = null)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}|{taskId}|{repeat}|{retryIndex}"));
        var unit = BitConverter.ToUInt32(hash) / (double)uint.MaxValue;
        var backoff = Delay(retryIndex) * (0.5 + 0.5 * unit);
        if (retryAfter is { } ra && ra > backoff) return ra < MaxRetryAfter ? ra : MaxRetryAfter;
        return backoff > TimeSpan.FromMilliseconds(MaxDelayMs) ? TimeSpan.FromMilliseconds(MaxDelayMs) : backoff;
    }
}
