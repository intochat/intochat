using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record BundleNameIsValid(string BundleName, string Key) : Rule("apps.bundle-name")
{
    public override bool Broken => BundleName != Key || string.IsNullOrWhiteSpace(BundleName)
        || BundleName.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-');
    public override string Message => $"'{BundleName}' is not a valid app name: lowercase letters, digits and dashes, matching the key.";
}
