using System.Text.Json;
using Fransync.Client.Configuration;
using Fransync.Client.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fransync.Client.Console;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();

        try
        {
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            var logger = host.Services.GetService<ILogger<Program>>();
            logger?.LogCritical(ex, "Application terminated unexpectedly");
            throw;
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                if (context.HostingEnvironment.IsDevelopment())
                {
                    config.AddJsonFile("appsettings.Development.json", optional: false, reloadOnChange: true);
                }
            })
            .ConfigureServices((context, services) =>
            {
                // Configuration
                services.Configure<SyncClientOptions>(
                    context.Configuration.GetSection(SyncClientOptions.SectionName));

                // HTTP Client
                services.AddHttpClient<IBridgeHttpClient, BridgeHttpClient>();

                // Services
                services.AddSingleton<IFileBlockService, FileBlockService>();
                services.AddSingleton<IBridgeWebSocketClient, BridgeWebSocketClient>();
                services.AddSingleton<IFileWatcherService, FileWatcherService>();
                services.AddScoped<IFileSyncService, FileSyncService>();
                services.AddSingleton<IResilienceService, ResilienceService>();
                services.AddHttpClient<IFileDownloadService, FileDownloadService>();
                services.AddScoped<IWebSocketMessageHandler, WebSocketMessageHandler>();

                services.AddSingleton<IDirectorySnapshotService, DirectorySnapshotService>();
                services.AddHttpClient<IDirectorySnapshotService, DirectorySnapshotService>();
                services.AddScoped<IManifestGenerationService, ManifestGenerationService>();

                // Hosted Service
                services.AddHostedService<SyncClientHostedService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            });
}