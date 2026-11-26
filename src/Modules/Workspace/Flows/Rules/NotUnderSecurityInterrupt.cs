using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows.Rules;

// Ino does not start a flow while a trusted security interrupt is active.
internal sealed record NotUnderSecurityInterrupt(string? Reason) : Rule("apps.flows-not-under-security-interrupt")
{
    public override bool Broken => Reason is not null;
    public override string Message => $"blocked by security interrupt: {Reason}";
}
