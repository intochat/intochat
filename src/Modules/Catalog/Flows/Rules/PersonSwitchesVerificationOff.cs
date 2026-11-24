using DigitalBrain.Access;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows.Rules;

// An engineering run passes every verification gate unless a person or the kernel started it with one switched off. An agent,
// an app and a person stamp that carries a conversation (the model) start runs, but only with every gate on.
internal sealed record PersonSwitchesVerificationOff(EngineerVerificationPolicy Policy, Caller? Caller) : Rule("apps.engineer-person-switches-verification-off")
{
    public override bool Broken => Policy.SkipsAny && Caller is not ({ Actor: Actor.Person, Conversation: null } or { Actor: Actor.Kernel });
    public override string Message => $"only a person or the kernel switches a verification gate off ({string.Join(", ", Policy.SkippedGates)}); start the run with every gate on";
}
