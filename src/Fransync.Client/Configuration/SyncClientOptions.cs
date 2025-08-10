namespace Fransync.Client.Configuration;

public class SyncClientOptions
{
    public const string SectionName = "SyncClient";

    public string MonitoredPath { get; set; } = @"C:\temp\SyncFolder";
    public string SyncWithPath { get; set; } = @"C:\temp\B-SyncFolder";
    public string BridgeEndpoint { get; set; } = "http://localhost:5246/sync";
    public string BridgeWebSocketUrl { get; set; } = "ws://localhost:5246/ws/client";
    public int BlockSize { get; set; } = 256 * 1024; // 256 KB
    public int FileChangeDelayMs { get; set; } = 300;

    // Polly Retry Policy Configuration
    public ResiliencePolicyOptions ResiliencePolicy { get; set; } = new();
}