using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

// Keys that start with "__" are the kernel's own (the compiled type the host loads, for one) and never arrive through
// IApp.Configure: a caller allowed to configure an app must not be able to make the host load OS code under its settings.
internal sealed record SettingsAreNotReserved(IReadOnlyCollection<string> Keys) : Rule("apps.settings-reserved")
{
    public override bool Broken => Keys.Any(static key => key.StartsWith("__", StringComparison.Ordinal));
    public override string Message => "Settings whose keys start with \"__\" are reserved to the kernel.";
}
