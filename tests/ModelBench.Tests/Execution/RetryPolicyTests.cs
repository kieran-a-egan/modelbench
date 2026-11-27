using ModelBench.Core.Execution;

namespace ModelBench.Tests.Execution;

public class RetryPolicyTests
{
    [Fact] public void RetryAfterAboveMaxDelayIsHonouredUpToTenMinutes()
    {
        var p = new RetryPolicy();
        Assert.Equal(TimeSpan.FromSeconds(120), p.Delay(0, Guid.Empty, Guid.Empty, 0, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromMinutes(10), p.Delay(0, Guid.Empty, Guid.Empty, 0, TimeSpan.FromHours(2)));
    }

    [Fact] public void BackoffStaysCappedAtMaxDelay()
        => Assert.True(new RetryPolicy().Delay(20, Guid.Empty, Guid.Empty, 0) <= TimeSpan.FromSeconds(30));
}
