namespace Fransync.Client.Configuration;

public class ResiliencePolicyOptions
{
    public int MaxRetryAttempts { get; set; } = 10;
    public int BaseDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 300000; // 5 minutes
    public double BackoffMultiplier { get; set; } = 2.0;
    public bool UseJitter { get; set; } = true;

    // Circuit Breaker Settings
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public TimeSpan CircuitBreakerSamplingDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan CircuitBreakerMinimumThroughput { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}