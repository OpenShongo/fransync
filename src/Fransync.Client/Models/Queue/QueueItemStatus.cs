using System.Text.Json.Serialization;

namespace Fransync.Client.Models.Queue;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueItemStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Retrying
}