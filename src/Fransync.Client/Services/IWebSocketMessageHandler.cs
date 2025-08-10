using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IWebSocketMessageHandler
{
    Task HandleMessageAsync(string message, CancellationToken cancellationToken = default);
}