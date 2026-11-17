using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Identity.Rules;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Secrets;

// One principal's vault, in the system brain. The key is the principal id (it may contain '/').
[NeuronAlias("identity.vault"), DefaultNeuronType(IOsVault.Type)]
internal interface IOsVault
{
    const string Type = "identity.vault";

    static string Key(string principal) => "system/" + principal;

    Task<SecretRef> Set(string name, string label, string value);
    Task Remove(string name);
    Task<string> Resolve(SecretRef secret);
    Task<string[]> Values();
}

[DigitalBrain.Signal("identity.vault-changed")]
internal sealed record OsVaultChanged(string Name) : DigitalBrain.Signal;

[Neuron(IOsVault.Type)]
internal sealed class OsVaultNeuron(IKeyWrapper keys) : Neuron<SecretsState>, IOsVault
{
    private readonly SecretsStore _secrets = new(keys);

    public async Task<SecretRef> Set(string name, string label, string value)
    {
        await Check("Set", new CallerCanStoreSecret(Context.Caller, Principal()));

        var next = Copy(State);
        next.Owner = Principal();
        var reference = _secrets.Set(next, name, label, value);
        await Save(next, new OsVaultChanged(name));
        return reference;
    }

    public async Task Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await Check("Remove", new CallerCanRemoveSecret(Context.Caller, Principal()));

        if (!Copy(State).Fields.ContainsKey(name)) { return; }
        var next = Copy(State);
        next.Fields.Remove(name);
        await Save(next, new OsVaultChanged(name));
    }

    public Task<string> Resolve(SecretRef secret)
    {
        // An outbound call: an app acting for a person (the app proxy stamps it), or platform code in the kernel scope.
        // ...and an app only for the person whose vault this is.
        if (!(CallerRules.IsApp(Context.Caller, out _) && CallerRules.ActsFor(Context.Caller) == Principal()) && !CallerRules.IsKernel(Context.Caller))
        {
            throw new UntrustedCallerException("Only a trusted outbound call can resolve a secret.");
        }

        var next = Copy(State);
        next.Owner = Principal();
        return Task.FromResult(_secrets.Resolve(next, secret));
    }

    public Task<string[]> Values()
    {
        if (!CallerRules.IsDirectPerson(Context.Caller, Principal()) && !CallerRules.IsKernel(Context.Caller))
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
        Fields = new Dictionary<string, OsSecretRecord>(state.Fields),
    };
}
