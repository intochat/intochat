using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.AI.Agents.Context.Rules;

// A user's or the platform's interrupt entry is not overwritten or removed by a model or an app. Unstamped is a call that runs inside
// a neuron turn with no capability caller; every such production writer is platform code (audited for Task 20), so it is let through
// here, while Set still stamps the entry it writes as app (an unstamped caller reads as app everywhere else).
internal sealed record InterruptClearedByTrust(bool Held, bool Unstamped, string? CallerTrust) : Rule("ai.context-interrupt-cleared-by-trust")
{
    public override bool Broken => Held && !Unstamped && CallerTrust is ContextTrust.Model or ContextTrust.App;
    public override string Message => "interrupt entries are cleared by their trust level only";
}
