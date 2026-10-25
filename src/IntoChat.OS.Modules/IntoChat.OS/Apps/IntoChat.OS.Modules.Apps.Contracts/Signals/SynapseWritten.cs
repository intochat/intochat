namespace IntoChat.OS.Modules.Apps.Signals;

// A sentence became a live synapse of this app: Hash is the deployed behavior's assembly hash, the one its
// run facts carry; ElapsedMs runs from the request to the deploy; Attempts counts the model calls.
[GenerateSerializer, Alias("apps.synapse-written")]
public sealed record SynapseWritten(
    [property: Id(0)] string Path,
    [property: Id(1)] string Hash,
    [property: Id(2)] long ElapsedMs,
    [property: Id(3)] int Attempts) : Signal;
