using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Button.Signals;

[GenerateSerializer, Alias("ui.button-clicked")]
public sealed record ButtonClicked([property: Id(0)] string Name, [property: Id(1)] string Action,
    [property: Id(2)] Guid RequestId = default) : Signal;
