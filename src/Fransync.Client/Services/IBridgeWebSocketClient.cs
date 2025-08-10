namespace Fransync.Client.Services;

public interface IBridgeWebSocketClient
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task ConnectWithRetryAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    event Action<string> MessageReceived;
    event Action<string> ConnectionStatusChanged;
    bool IsConnected { get; }
}