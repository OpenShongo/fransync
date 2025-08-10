
using Fransync.Bridging.WebService.Middleware;
using Fransync.Bridging.WebService.Services;
using System.Text.Json;
using WebSocketManager = Fransync.Bridging.WebService.Services.WebSocketManager;

namespace Fransync.Bridging.WebService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        // Register services
        builder.Services.AddSingleton<ISyncStore, InMemorySyncStore>();
        builder.Services.AddSingleton<IWebSocketManager, WebSocketManager>();

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();

        app.UseWebSockets();
        app.UseMiddleware<WebSocketClientMiddleware>();

        app.MapControllers();

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            service = "Fransync.Bridging.WebService"
        }));

        app.Run();
    }
}