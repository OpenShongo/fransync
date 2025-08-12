
using Fransync.Bridging.WebService.Middleware;
using Fransync.Bridging.WebService.Services;
using System.Reflection;
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

        if (!app.Environment.IsProduction() ||
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true")
        {
            app.UseHttpsRedirection();
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

        // Dedicated version endpoint - comprehensive version information
        app.MapGet("/version", () =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "unknown";
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
            var pipelineVersion = Environment.GetEnvironmentVariable("VERSION");
            var buildDate = assembly.GetCustomAttribute<AssemblyMetadataAttribute>("BuildDate")?.Value;

            return Results.Ok(new
            {
                service = "Fransync.Bridging.WebService",
                version = new
                {
                    assembly = version,
                    informational = informationalVersion,
                    pipeline = pipelineVersion ?? "unknown",
                    display = pipelineVersion ?? informationalVersion
                },
                build = new
                {
                    date = buildDate ?? "unknown",
                    environment = app.Environment.EnvironmentName,
                    framework = Environment.Version.ToString(),
                    dotnetVersion = Environment.GetEnvironmentVariable("DOTNET_VERSION") ?? "unknown"
                },
                timestamp = DateTime.UtcNow
            });
        });

        app.Run();
    }
}