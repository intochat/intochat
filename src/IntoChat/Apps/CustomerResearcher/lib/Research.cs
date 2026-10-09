// Shared by the researcher's synapses: the durable queue entry the worker writes and the activity
// projection reads. One record, one meaning.
public sealed record QueuedResearch(string Id, string Query, string Status, string? Outcome);
