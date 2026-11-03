using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record SynapseRequestIsPending(bool Pending) : Rule("apps.synapse-pending")
{
    public override bool Broken => !Pending;
    public override string Message => "No requested synapse waits under that operation id.";
}
