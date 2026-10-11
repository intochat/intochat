namespace DigitalBrain.Apps;

public enum SynapseStatus { Requested, Written, Rejected }

// One fast-path request on its app: the sentence, then the outcome the writer landed. Path and Hash name
// the synapse once it is deployed; Errors say why it was rejected; Base is the synapse it was forked from.
// Staged: the writer put the file into the app before deploying it; Replaced is what that path held before,
// restored when the deploy is rejected (null: the file was new).
[GenerateSerializer, Alias("apps.synapse")]
public sealed record AppSynapse(
    [property: Id(0)] Guid OperationId,
    [property: Id(1)] string Sentence,
    [property: Id(2)] string[] Focus,
    [property: Id(3)] SynapseStatus Status,
    [property: Id(4)] DateTimeOffset RequestedAt,
    [property: Id(5)] string[] Errors,
    [property: Id(6)] string? Path = null,
    [property: Id(7)] string? Hash = null,
    [property: Id(8)] long ElapsedMs = 0,
    [property: Id(9)] int Attempts = 0,
    [property: Id(10)] string? Base = null,
    [property: Id(11)] bool Staged = false,
    [property: Id(12)] string? Replaced = null);

// What the runtime runs for an app: its program files and the configuration they read.
[GenerateSerializer, Alias("apps.program")]
public sealed record AppProgram(
    [property: Id(0)] Dictionary<string, string> Sources,
    [property: Id(1)] Dictionary<string, string> Configuration);
