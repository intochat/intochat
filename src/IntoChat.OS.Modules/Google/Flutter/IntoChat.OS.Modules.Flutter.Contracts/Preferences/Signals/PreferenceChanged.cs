using IntoChat.OS;

namespace IntoChat.OS.Flutter.Preferences.Signals;

[GenerateSerializer, Alias("ui.preference-changed")]
public sealed record PreferenceChanged([property: Id(0)] PreferenceStore Store) : Signal;
