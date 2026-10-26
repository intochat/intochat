namespace IntoChat.OS.Modules.Apps.Signals;

// A sentence the fast path could not turn into a synapse that has the shape and compiles; nothing was deployed.
[DigitalBrain.Signal("apps.synapse-rejected")]
public sealed record SynapseRejected(
    string Sentence,
    string[] Errors) : DigitalBrain.Signal;
