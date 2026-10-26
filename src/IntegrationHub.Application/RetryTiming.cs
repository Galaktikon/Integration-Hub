namespace IntegrationHub.Application;

public static class RetryTiming
{
    public static TimeSpan Delay(RetryPolicy policy, int attemptInGeneration,
        TimeSpan? retryAfter = null, double jitter = 0)
    {
        if (attemptInGeneration < 1 || jitter is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(attemptInGeneration));
        var baseSeconds = Math.Min(policy.MaxDelaySeconds,
            policy.BaseDelaySeconds * Math.Pow(2, Math.Min(attemptInGeneration - 1, 30)));
        var jittered = Math.Min(policy.MaxDelaySeconds, baseSeconds * (1 + 0.2 * jitter));
        if (retryAfter is { } requested && requested > TimeSpan.Zero)
            jittered = Math.Max(jittered, Math.Min(policy.MaxDelaySeconds, requested.TotalSeconds));
        return TimeSpan.FromSeconds(jittered);
    }

    public static bool IsRetryableCategory(string? category) =>
        category is "transient" or "timeout";
}
