using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record PresentationIsPublished(bool Published) : Rule("apps.presentation-published")
{
    public override bool Broken => !Published;
    public override string Message => "Publish a presentation before writing its blackboard.";
}
