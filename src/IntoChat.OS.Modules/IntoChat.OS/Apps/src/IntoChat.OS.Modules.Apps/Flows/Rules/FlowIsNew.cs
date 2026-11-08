using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows.Rules;

// A caller-chosen flow key starts one flow: a second Start with it is refused.
internal sealed record FlowIsNew(string FlowId, bool Exists) : Rule("apps.flows-flow-is-new")
{
    public override bool Broken => Exists;
    public override string Message => $"flow exists: {FlowId}";
}
