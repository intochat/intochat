using DigitalBrain.Flows;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows.Rules;

// A caller-chosen flow key starts one flow: a second Start with it is refused.
internal sealed record FlowIsNew(string FlowId, string? RequestedKey, FlowEntry? HeldEntry, FlowStatus? HeadStatus) : Rule("apps.flows-flow-is-new")
{
    public override bool Broken => RequestedKey is not null && (HeldEntry is not null || (HeadStatus is { } status && status != FlowStatus.Pending));
    public override string Message => $"flow exists: {FlowId}";
}
