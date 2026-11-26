using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record VersionExists(string Version, string[] Known) : Rule("apps.version-exists")
{
    public override bool Broken => !Known.Contains(Version, StringComparer.Ordinal);
    public override string Message => $"No version {Version}. This app has: {string.Join(", ", Known)}.";
}
