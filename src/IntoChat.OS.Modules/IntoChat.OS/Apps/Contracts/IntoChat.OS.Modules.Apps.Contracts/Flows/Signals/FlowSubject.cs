namespace IntoChat.OS.Modules.Apps.Flows;

// A flow names what it is about; the index folds it into FlowView.Subject.
[DigitalBrain.Signal("flow.subject")]
public sealed record FlowSubject(string FlowId, string Subject) : DigitalBrain.Signal;
