using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Modules.Identity.Contracts.Grants;

[DigitalBrain.Contracts.NeuronAlias(TypeName), DigitalBrain.Contracts.DefaultNeuronType(TypeName)]
public interface IGrantsNeuron
{
    const string TypeName = "grants.default";
    const string DefaultKey = "grants/default";
    static string Key(string brain) => $"{brain}/grants/default";

    Task Grant(Grant grant);
    Task Revoke(string grantId);
    Task<Grant[]> List();
    Task<long> GetVersion();
}

[DigitalBrain.Signal("identity.grants-changed")]
public sealed record GrantsChanged(long Version) : DigitalBrain.Signal;

[GenerateSerializer, Alias("identity.grants-state")]
public sealed record GrantsState
{
    [Id(0)] public Grant[] Grants { get; init; } = [];
    [Id(1)] public long Version { get; init; }
}
