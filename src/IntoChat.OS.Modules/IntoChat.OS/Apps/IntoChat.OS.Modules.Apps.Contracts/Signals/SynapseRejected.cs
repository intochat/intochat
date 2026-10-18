namespace IntoChat.OS.Apps.Signals;

// A sentence the fast path could not turn into a synapse that has the shape and compiles; nothing was deployed.
[GenerateSerializer, Alias("apps.synapse-rejected")]
public sealed record SynapseRejected(
    [property: Id(0)] string Sentence,
    [property: Id(1)] string[] Errors) : Signal;
