using IntoChat.OS;

namespace IntoChat.OS.Flutter.Entries.Signals;

[GenerateSerializer, Alias("ui.entry-control-changed")]
public sealed record EntryControlChanged([property: Id(0)] string Name) : Signal;
