using Fransync.Client.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Fransync.Client.Services;

public class ResilienceService : IResilienceService
{
    private readonly ResiliencePolicyOptions _options;
    private readonly ILogger<ResilienceService> _logger;
    private readonly ResiliencePipeline _webSocketPipeline;
    private readonly ResiliencePipeline _httpPipeline;

    public ResilienceService(IOptions<SyncClientOptions> options, ILogger<ResilienceService> logger)
    {
        _options = options.Value.ResiliencePolicy;
        _logger = logger;

        _webSocketPipeline = CreateWebSocketPipeline();
        _httpPipeline = CreateHttpPipeline();
    }

    public ResiliencePipeline GetWebSocketRetryPipeline() => _webSocketPipeline;
    public ResiliencePipeline GetHttpRetryPipeline() => _httpPipeline;

    private ResiliencePipeline CreateWebSocketPipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = _options.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(_options.BaseDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(_options.MaxDelayMs),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = _options.UseJitter,
                ShouldHandle = new PredicateBuilder().Handle<WebSocketException>()
                    .Handle<SocketException>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning("WebSocket connection retry attempt {AttemptNumber} after {Delay}ms. Exception: {Exception}",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.8,
                MinimumThroughput = 3,
                SamplingDuration = _options.CircuitBreakerSamplingDuration,
                BreakDuration = _options.CircuitBreakerBreakDuration,
                ShouldHandle = new PredicateBuilder().Handle<WebSocketException>()
                    .Handle<SocketException>(),
                OnOpened = _ =>
                {
                    _logger.LogWarning("WebSocket circuit breaker opened");
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _logger.LogInformation("WebSocket circuit breaker closed");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();
    }

    private ResiliencePipeline CreateHttpPipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult((HttpResponseMessage response) =>
                        !response.IsSuccessStatusCode &&
                        (response.StatusCode >= HttpStatusCode.InternalServerError ||
                         response.StatusCode == HttpStatusCode.RequestTimeout)),
                OnRetry = args =>
                {
                    _logger.LogWarning("HTTP request retry attempt {AttemptNumber} after {Delay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(10))
            .Build();
    }
}