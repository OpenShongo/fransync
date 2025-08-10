using Polly;

namespace Fransync.Client.Services;

public interface IResilienceService
{
    ResiliencePipeline GetWebSocketRetryPipeline();
    ResiliencePipeline GetHttpRetryPipeline();
}