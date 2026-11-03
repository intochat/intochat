using DigitalBrain.Capabilities;
using DigitalBrain.Contracts;
using IntoChat.OS.Authority.Identity.Rules;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Secrets;

// One principal's vault, in the system brain. The key is the principal id (it may contain '/').
[NeuronAlias("identity.vault"), DefaultNeuronType(IVault.Type)]
internal interface IVault
{
    const string Type = "identity.vault";

    static string Key(string principal) => "system/" + principal;

    Task<SecretRef> Set(CapabilityCaller caller, string name, string label, string value);
    Task Remove(CapabilityCaller caller, string name);
    Task<string> Resolve(CapabilityCaller caller, SecretRef secret);
    Task<string[]> Values(CapabilityCaller caller);
}

[DigitalBrain.Signal("identity.vault-changed")]
internal sealed record VaultChanged(string Name) : DigitalBrain.Signal;

[DigitalBrain.Neuron(IVault.Type)]
internal sealed class VaultNeuron(IKeyWrapper keys) : DigitalBrain.Neuron<SecretsState>, IVault
{
    private readonly SecretsStore _secrets = new(keys);

    public async Task<SecretRef> Set(CapabilityCaller caller, string name, string label, string value)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await Check("Set", new CallerCanStoreSecret(!(caller.Kind == CallerKind.User && caller.PrincipalId != Principal())));

        var next = Copy(State);
        next.Owner = Principal();
        var reference = _secrets.Set(next, name, label, value);
        await Save(next, new VaultChanged(name));
        return reference;
    }

    public async Task Remove(CapabilityCaller caller, string name)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var isOwner = caller.Kind == CallerKind.User && caller.PrincipalId == Principal();
        var isPlatform = caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform;
        await Check("Remove", new CallerCanRemoveSecret(isOwner || isPlatform));

        if (!Copy(State).Fields.ContainsKey(name)) { return; }
        var next = Copy(State);
        next.Fields.Remove(name);
        await Save(next, new VaultChanged(name));
    }

    public Task<string> Resolve(CapabilityCaller caller, SecretRef secret)
    {
        if (caller is null || caller.Kind == CallerKind.User
            || caller.StampedBy is not (TrustedEdge.AppProxy or TrustedEdge.Platform)
            || string.IsNullOrEmpty(caller.AppId))
        {
            throw new UntrustedCallerException("Only a trusted outbound call can resolve a secret.");
        }

        var next = Copy(State);
        next.Owner = Principal();
        return Task.FromResult(_secrets.Resolve(next, secret));
    }

    public Task<string[]> Values(CapabilityCaller caller)
    {
        if (caller is null || !(caller.PrincipalId == Principal() || (caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform)))
        {
            throw new UntrustedCallerException("Only the vault's owner can list its values.");
        }

        return Task.FromResult(_secrets.Values(State));
    }

    private string Principal() => Context.Id.Key;

    private static SecretsState Copy(SecretsState state) => new()
    {
        Owner = state.Owner,
        WrappedOwnerKey = state.WrappedOwnerKey,
        Fields = new Dictionary<string, SecretRecord>(state.Fields),
    };
}
