using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record AssignmentIsCurrent(bool Replaced) : Rule("apps.assignment-current")
{
    public override bool Broken => Replaced;
    public override string Message => "The program was replaced; this assignment may not publish.";
}
