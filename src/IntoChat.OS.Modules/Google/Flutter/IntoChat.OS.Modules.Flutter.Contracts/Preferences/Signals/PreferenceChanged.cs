using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Preferences.Signals;

[GenerateSerializer, Alias("ui.preference-changed")]
public sealed record PreferenceChanged([property: Id(0)] PreferenceStore Store) : Signal;
