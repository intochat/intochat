namespace IntoChat.OS.Modules.Apps;

// Unsettled: the deploy never answered and putting the runtime back did not answer either, so what the
// runtime runs is unknown; the request keeps its staged source.
public enum SynapseStatus { Requested, Written, Rejected, Unsettled }

// One fast-path request on its app: the sentence, then the outcome the writer landed. Path and Hash name
// the synapse once it is deployed; Errors say why it was rejected; Base is the synapse it was forked from.
// Source is the staged synapse while its deploy runs: it joins the app's files only once it is Written, so
// nothing else (exports, other deploys, other writes' compile gates) ever sees a file the runtime may not run.
public sealed record AppSynapse(
    Guid OperationId,
    string Sentence,
    string[] Focus,
    SynapseStatus Status,
    DateTimeOffset RequestedAt,
    string[] Errors,
    string? Path = null,
    string? Hash = null,
    long ElapsedMs = 0,
    int Attempts = 0,
    string? Base = null,
    string? Source = null);
