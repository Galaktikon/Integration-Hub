using IntegrationHub.Application;

namespace IntegrationHub.Tests;

public sealed class RetryTimingTests
{
    [Fact]
    public void ExponentialDelayHonorsCapAndRetryAfter()
    {
        var policy = new RetryPolicy(4, 2, 10, 2);
        Assert.Equal(TimeSpan.FromSeconds(2), RetryTiming.Delay(policy, 1));
        Assert.Equal(TimeSpan.FromSeconds(4), RetryTiming.Delay(policy, 2));
        Assert.Equal(TimeSpan.FromSeconds(10), RetryTiming.Delay(policy, 9));
        Assert.Equal(TimeSpan.FromSeconds(8), RetryTiming.Delay(policy, 1, TimeSpan.FromSeconds(8)));
        Assert.True(RetryTiming.Delay(policy, 1, jitter: 1) <= TimeSpan.FromSeconds(10));
        Assert.True(RetryTiming.IsRetryableCategory("transient"));
        Assert.False(RetryTiming.IsRetryableCategory("authentication"));
    }
}
