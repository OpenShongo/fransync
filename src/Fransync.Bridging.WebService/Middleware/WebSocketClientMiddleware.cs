using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Fransync.Bridging.WebService.Models;
using Fransync.Bridging.WebService.Services;

namespace Fransync.Bridging.WebService.Middleware;


public class WebSocketClientMiddleware(RequestDelegate next, IWebSocketManager wsManager)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/ws/client")
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var clientId = wsManager.AddClient(webSocket);

            // Send welcome message
            await wsManager.SendToClientAsync(clientId, new WebSocketMessage
            {
                Type = "connected",
                Data = new { ClientId = clientId, Message = "Connected to sync bridge server" }
            });

            var buffer = new byte[1024 * 4];
            while (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        wsManager.RemoveClient(clientId);
                        break;
                    }

                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"[WS MESSAGE FROM {clientId}] {msg}");

                    // Handle incoming messages from clients
                    await HandleClientMessage(clientId, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WS ERROR] Client {clientId}: {ex.Message}");
                    wsManager.RemoveClient(clientId);
                    break;
                }
            }
        }
        else
        {
            await next(context);
        }
    }

    private async Task HandleClientMessage(string clientId, string message)
    {
        try
        {
            var wsMessage = JsonSerializer.Deserialize<WebSocketMessage>(message);

            // Handle different message types from clients
            switch (wsMessage?.Type?.ToLowerInvariant())
            {
                case "ping":
                    await wsManager.SendToClientAsync(clientId, new WebSocketMessage
                    {
                        Type = "pong",
                        Data = new { Timestamp = DateTime.UtcNow }
                    });
                    break;

                case "request_status":
                    await wsManager.SendToClientAsync(clientId, new WebSocketMessage
                    {
                        Type = "status",
                        Data = new
                        {
                            ConnectedClients = wsManager.GetConnectedClientsCount(),
                            ServerTime = DateTime.UtcNow
                        }
                    });
                    break;

                default:
                    Console.WriteLine($"[WS] Unknown message type: {wsMessage?.Type}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[WS] Invalid JSON from client {clientId}: {ex.Message}");
        }
    }
}