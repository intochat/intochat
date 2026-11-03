using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record SynapseSentenceFits(int Length) : Rule("apps.synapse-sentence")
{
    public override bool Broken => Length > 4000;
    public override string Message => "Say the synapse in at most 4000 characters.";
}
