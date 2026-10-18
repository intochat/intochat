namespace DigitalBrain.Flutter.Chat;

[GenerateSerializer, Alias("ui.chat-role")]
public enum ChatRole { User, Assistant }

[GenerateSerializer, Alias("ui.chat-entry")]
public sealed record ChatEntry([property: Id(0)] string Id, [property: Id(1)] ChatRole Role, [property: Id(2)] string Text)
{
    [Id(3)] public string Status { get; init; } = "completed";
    [Id(4)] public string? Error { get; init; }
    [Id(5)] public ChatActivity[] Activities { get; init; } = [];
}

[GenerateSerializer, Alias("ui.chat-activity")]
public sealed record ChatActivity(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Input,
    [property: Id(3)] string? Result = null,
    [property: Id(4)] string? Error = null);
