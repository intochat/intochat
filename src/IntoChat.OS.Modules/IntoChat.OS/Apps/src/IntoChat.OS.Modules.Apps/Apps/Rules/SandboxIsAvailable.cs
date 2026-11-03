using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record SandboxIsAvailable(bool CanRun) : Rule("apps.sandbox")
{
    public override bool Broken => !CanRun;
    public override string Message => AppAuthoringPolicy.SandboxMissing;
}
