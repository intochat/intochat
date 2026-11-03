using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record PresentationHasViews(int Count) : Rule("apps.presentation-views")
{
    public override bool Broken => Count <= 0 || Count > 16;
    public override string Message => Count <= 0
        ? "Publish at least one named view."
        : "A presentation has at most 16 uniquely named views with roots.";
}
