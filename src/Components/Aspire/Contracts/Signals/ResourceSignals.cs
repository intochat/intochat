using System.Text.Json.Serialization;
using DigitalBrain;

namespace IntoChat.Aspire;

[Signal("aspire.resource.changed")]
public sealed record AspireResourceChanged(string Name, string InstanceId, string State, string? Health) : Signal;

[Signal("aspire.command.requested")]
public sealed record AspireCommandRequested(string Resource, string Command, string Confirmation) : Signal;

[Signal("aspire.command.executed")]
public sealed record AspireCommandExecuted(string Resource, string Command, DateTimeOffset At) : Signal;

[Signal("aspire.command.refused")]
public sealed record AspireCommandRefused(
    string Resource,
    string Command,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null) : Signal;
