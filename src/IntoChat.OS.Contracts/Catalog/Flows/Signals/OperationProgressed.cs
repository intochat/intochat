namespace IntoChat.OS.Modules.Apps.Flows;

[DigitalBrain.Signal(SignalAlias)]
public sealed record OperationProgressed(
    string FlowId,
    string WorktreeId,
    string NodeId,
    string SliceId,
    EngineeringHarnessKind Harness,
    int Percent,
    string Phase,
    string Detail) : DigitalBrain.Signal
{
    public const string SignalAlias = "engineer.operation-progressed";
}
