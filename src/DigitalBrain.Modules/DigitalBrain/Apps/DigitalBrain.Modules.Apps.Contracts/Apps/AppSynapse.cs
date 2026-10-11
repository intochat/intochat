namespace DigitalBrain.Apps;

// Unsettled: the deploy never answered and putting the runtime back did not answer either, so what the
// runtime runs is unknown; the request keeps its staged source.
public enum SynapseStatus { Requested, Written, Rejected, Unsettled }

// One fast-path request on its app: the sentence, then the outcome the writer landed. Path and Hash name
// the synapse once it is deployed; Errors say why it was rejected; Base is the synapse it was forked from.
// Source is the staged synapse while its deploy runs: it joins the app's files only once it is Written, so
// nothing else (exports, other deploys, other writes' compile gates) ever sees a file the runtime may not run.
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
    [property: Id(11)] string? Source = null);
