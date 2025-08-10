using Fransync.Client.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using System.Net.WebSockets;
using System.Text;

namespace Fransync.Client.Services;

public class BridgeWebSocketClient : IBridgeWebSocketClient, IDisposable
{
    private readonly SyncClientOptions _options;
    private readonly IResilienceService _resilienceService;
    private readonly ILogger<BridgeWebSocketClient> _logger;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isReconnecting = false;

    public event Action<string>? MessageReceived;
    public event Action<string>? ConnectionStatusChanged;
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public BridgeWebSocketClient(
        IOptions<SyncClientOptions> options,
        IResilienceService resilienceService,
        ILogger<BridgeWebSocketClient> logger)
    {
        _options = options.Value;
        _resilienceService = resilienceService;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _webSocket = new ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();

            await _webSocket.ConnectAsync(new Uri(_options.BridgeWebSocketUrl), cancellationToken);
            _logger.LogInformation("[WEBSOCKET] Connected to bridge");
            ConnectionStatusChanged?.Invoke("Connected");

            _ = Task.Run(() => ListenForMessagesAsync(_cancellationTokenSource.Token), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WEBSOCKET ERROR] Failed to connect");
            ConnectionStatusChanged?.Invoke("Disconnected");
            throw;
        }
    }

    public async Task ConnectWithRetryAsync(CancellationToken cancellationToken = default)
    {
        var pipeline = _resilienceService.GetWebSocketRetryPipeline();

        try
        {
            await pipeline.ExecuteAsync(async (cancellationToken) =>
            {
                await ConnectAsync(cancellationToken);
            }, cancellationToken);

            _isReconnecting = false;
            _logger.LogInformation("[WEBSOCKET] Successfully connected with retry policy");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WEBSOCKET] Failed to connect after all retry attempts");
            ConnectionStatusChanged?.Invoke("Failed");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _isReconnecting = false;

        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", cancellationToken);
        }

        _cancellationTokenSource?.Cancel();
        _webSocket?.Dispose();
        _cancellationTokenSource?.Dispose();

        _logger.LogInformation("[WEBSOCKET] Disconnected from bridge");
        ConnectionStatusChanged?.Invoke("Disconnected");
    }

    private async Task ListenForMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("[WEBSOCKET] Connection closed by bridge");
                    ConnectionStatusChanged?.Invoke("Disconnected");

                    if (!cancellationToken.IsCancellationRequested && !_isReconnecting)
                    {
                        _isReconnecting = true;
                        _ = Task.Run(() => ConnectWithRetryAsync(cancellationToken), cancellationToken);
                    }
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogInformation("[WEBSOCKET MESSAGE] {Message}", message);
                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[WEBSOCKET] Listening cancelled");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[WEBSOCKET ERROR] Error in message listening");
            ConnectionStatusChanged?.Invoke("Disconnected");

            if (!_isReconnecting)
            {
                _isReconnecting = true;
                _ = Task.Run(() => ConnectWithRetryAsync(cancellationToken), cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _webSocket?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}