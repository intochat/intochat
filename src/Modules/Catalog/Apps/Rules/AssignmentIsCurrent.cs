using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record AssignmentIsCurrent(long ExpectedProgramRevision, long CurrentProgramRevision) : Rule("apps.assignment-current")
{
    public override bool Broken => ExpectedProgramRevision != CurrentProgramRevision;
    public override string Message => "The program was replaced; this assignment may not publish.";
}
