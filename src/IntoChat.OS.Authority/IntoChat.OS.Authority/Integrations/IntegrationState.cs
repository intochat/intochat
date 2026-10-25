using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Authority.Integrations;

// Secret fields sealed in the neuron's own state, plus the values of non-secret settings.
[GenerateSerializer, Alias("integration.registration.state")]
internal sealed class IntegrationState
{
    [Id(0)] public SealedSecret[] Secrets { get; set; } = [];

    // Non-secret setting values only; secret fields never appear here.
    [Id(1)] public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    // Bumped by every write, so holders of a released secret can tell a rotation from no change.
    [Id(2)] public long Revision { get; set; }

    // Fields a person set through the API; a seed never overwrites them. Every other stored value is
    // the deployment's and is reconciled when the deployment's value moves.
    [Id(3)] public string[] PersonFields { get; set; } = [];

    // Fields a person cleared; a seed never restores them. Setting the field again removes it.
    [Id(4)] public string[] ClearedFields { get; set; } = [];
}
