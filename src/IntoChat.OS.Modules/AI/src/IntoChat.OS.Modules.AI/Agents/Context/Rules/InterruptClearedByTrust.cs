using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.AI.Agents.Context.Rules;

// A user's or the platform's interrupt entry is not overwritten or removed by a model or an app.
internal sealed record InterruptClearedByTrust(bool Held, string? CallerTrust) : Rule("ai.context-interrupt-cleared-by-trust")
{
    public override bool Broken => Held && CallerTrust is ContextTrust.Model or ContextTrust.App;
    public override string Message => "interrupt entries are cleared by their trust level only";
}
