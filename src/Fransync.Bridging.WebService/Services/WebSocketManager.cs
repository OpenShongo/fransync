using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Fransync.Bridging.WebService.Services;

public class WebSocketManager : IWebSocketManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

    public string AddClient(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        _clients[id] = socket;
        return id;
    }

    public void RemoveClient(string clientId)
    {
        _clients.TryRemove(clientId, out _);
        Console.WriteLine($"[WS DISCONNECTED] Client {clientId}");
    }

    public async Task BroadcastToAllAsync(object message)
    {
        var json = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(json);
        
        var tasks = new List<Task>();
        
        foreach (var client in _clients.ToList())
        {
            if (client.Value.State == WebSocketState.Open)
            {
                tasks.Add(SendToClientAsync(client.Key, buffer));
            }
            else
            {
                // Remove disconnected clients
                _clients.TryRemove(client.Key, out _);
            }
        }
        
        await Task.WhenAll(tasks);
    }

    public async Task SendToClientAsync(string clientId, object message)
    {
        var json = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(json);
        await SendToClientAsync(clientId, buffer);
    }

    private async Task SendToClientAsync(string clientId, byte[] buffer)
    {
        if (_clients.TryGetValue(clientId, out var socket) && socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(buffer), 
                    WebSocketMessageType.Text, 
                    true, 
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS ERROR] Failed to send to client {clientId}: {ex.Message}");
                _clients.TryRemove(clientId, out _);
            }
        }
    }

    public int GetConnectedClientsCount() => _clients.Count(c => c.Value.State == WebSocketState.Open);
}

public interface IWebSocketManager
{
    string AddClient(WebSocket socket);
    void RemoveClient(string clientId);
    Task BroadcastToAllAsync(object message);
    Task SendToClientAsync(string clientId, object message);
    int GetConnectedClientsCount();
}