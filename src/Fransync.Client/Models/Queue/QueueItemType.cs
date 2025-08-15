using System.Text.Json.Serialization;

namespace Fransync.Client.Models.Queue;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueItemType
{
    Upload,
    Download,
    FileOperation,
    Synchronization
}