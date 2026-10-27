namespace IntoChat.OS.Modules.Apps.Signals;

// A sentence became a live synapse of this app: Hash is the deployed behavior's assembly hash, the one its
// run facts carry; ElapsedMs runs from the request to the deploy; Attempts counts the model calls.
[DigitalBrain.Signal("apps.synapse-written")]
public sealed record SynapseWritten(
    string Path,
    string Hash,
    long ElapsedMs,
    int Attempts) : DigitalBrain.Signal;
