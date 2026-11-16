using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.AI.Agents.Context.Rules;

// A user's or the platform's interrupt entry is not overwritten or removed by a model or an app. A call with no capability caller reads
// as app everywhere (ContextTrust.Of(null)), so it is refused too; the platform clears an interrupt under its own stamp.
internal sealed record InterruptClearedByTrust(ContextEntry? HeldEntry, string CallerTrust) : Rule("ai.context-interrupt-cleared-by-trust")
{
    public override bool Broken
        => HeldEntry is { EffectiveTags: var tags }
            && tags.Contains(ContextTags.Interrupt)
            && HeldEntry.Provenance?.Trust is ContextTrust.User or ContextTrust.Platform
            && CallerTrust is ContextTrust.Model or ContextTrust.App;
    public override string Message => "interrupt entries are cleared by their trust level only";
}
