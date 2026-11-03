using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record SaveToSourceIsAvailable(bool Development, bool FromFeature, string AppName) : Rule("apps.save-to-source")
{
    public override bool Broken => !Development || !FromFeature;
    public override string Message => !Development
        ? "Save to source is only available in development."
        : $"{AppName} is not a copy of a feature from the features folder, so it has no source to save to.";
}
