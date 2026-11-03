using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record BundleVersionIsSemantic(string Version) : Rule("apps.bundle-version")
{
    public override bool Broken => Version.Split('.') is not { Length: 3 } parts
        || parts.Any(part => !part.All(char.IsAsciiDigit) || part.Length == 0);
    public override string Message => $"'{Version}' is not a version of the form major.minor.patch.";
}
