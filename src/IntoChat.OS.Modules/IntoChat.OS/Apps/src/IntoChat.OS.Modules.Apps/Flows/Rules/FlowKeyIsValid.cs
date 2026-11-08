using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows.Rules;

// A caller-chosen flow key is one neuron key segment: non-empty, without '/'.
internal sealed record FlowKeyIsValid(string? Key) : Rule("apps.flows-flow-key-is-valid")
{
    public override bool Broken => Key is not null && (Key.Length == 0 || Key.Contains('/', StringComparison.Ordinal));
    public override string Message => $"'{Key}' is not a flow key: non-empty, no '/'";
}
