using Fransync.Client.Configuration;
using Fransync.Client.Services;
using Fransync.Client.Services.Queue;
using Fransync.Client.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

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

                // These are critical!
                // FIXED: Register LocalQueueManager with proper factory
                services.AddScoped<ILocalQueueManager>(serviceProvider =>
                {
                    var options = serviceProvider.GetRequiredService<IOptions<SyncClientOptions>>().Value;
                    var logger = serviceProvider.GetRequiredService<ILogger<LocalQueueManager>>();

                    // Use MonitoredPath as the base path for .fransync directory
                    return new LocalQueueManager(options.MonitoredPath, logger);
                });

                // FIXED: Register LocalBlockStore with proper factory
                services.AddScoped<ILocalBlockStore>(serviceProvider =>
                {
                    var options = serviceProvider.GetRequiredService<IOptions<SyncClientOptions>>().Value;
                    var logger = serviceProvider.GetRequiredService<ILogger<LocalBlockStore>>();

                    // Use MonitoredPath as the base path for .fransync directory
                    return new LocalBlockStore(options.MonitoredPath, logger);
                });

                // Existing services
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