using System.Text.Json.Serialization;

namespace IntoChat.Aspire;

public static class AspireBridgeProtocol
{
    public const string SocketPathVariable = "IntoChatOS__Aspire__BridgeSocket";
    public const string SocketPathKey = "IntoChatOS:Aspire:BridgeSocket";
}

public sealed record BridgeSnapshot(
    string Name,
    string InstanceId,
    string Type,
    string? Parent,
    string State,
    string? Health,
    string[] Urls,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    AspireCommand[] Commands,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Removed = false);

public sealed record BridgeCall(
    string Op,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstanceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Command = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Tail = 0);

public sealed record BridgeCommandReply(
    bool Succeeded,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);

public sealed record BridgeConsoleReply(string[] Lines);
