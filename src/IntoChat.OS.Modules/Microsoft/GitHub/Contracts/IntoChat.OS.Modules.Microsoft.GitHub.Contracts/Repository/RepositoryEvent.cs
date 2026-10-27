namespace IntoChat.OS.Modules.Microsoft.GitHub;

// Installation, Repository and App are the delivery's routing hints; the neuron accepts only its own.
[GenerateSerializer, Alias("github.repository-event")]
public sealed record RepositoryEvent(
    [property: Id(0)] string DeliveryId,
    [property: Id(1)] string Event,
    [property: Id(2)] string? Action,
    [property: Id(3)] int? Number,
    [property: Id(4)] string BodyHash,
    [property: Id(5)] long? Installation = null,
    [property: Id(6)] long? Repository = null,
    [property: Id(7)] long? App = null);
