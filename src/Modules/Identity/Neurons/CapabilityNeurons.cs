using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Identity.Ports;

namespace IntoChat.OS.Modules.Identity.Neurons;

// The module's identity@1 neuron. State lives in the directory neurons the port calls. This type is what the
// module validator requires: a [Neuron] assignable to IIdentity. Gated members read the stamped caller.
[Neuron("identity")]
internal sealed class IdentityNeuron(DirectoryPort directory) : Neuron<IdentityNeuronState>, IIdentity
{
    public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken = default)
        => directory.ReadAccount(accountId, cancellationToken);

    public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken = default)
        => directory.OwnerOfBrain(brainId, cancellationToken);

    public Task EnsureAccount(Account account, CancellationToken cancellationToken = default)
        => directory.EnsureAccount(account, cancellationToken);

    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken = default)
        => directory.CreateBrain(accountId, principalId, operationId, displayName, kind, cancellationToken);

    public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken = default)
        => directory.EnsureBrain(owner, cancellationToken);

    public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken = default)
        => directory.ProvisionOwner(owner, cancellationToken);

    public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken = default)
        => directory.IsRegistered(principalId, cancellationToken);

    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken = default)
        => directory.RegisterPrincipal(principalId, displayName, cancellationToken);

    public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken = default)
        => directory.CompleteRegistration(principalId, cancellationToken);

    public Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken = default)
        => directory.BrainsOf(principalId, cancellationToken);

    public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken = default)
        => directory.KindOf(brainScopeId, cancellationToken);

    public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken = default)
        => directory.BrainExists(brainScopeId, cancellationToken);

    public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken = default)
        => directory.Membership(brainScopeId, principalId, cancellationToken);

    public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken = default)
        => directory.EnsureMember(member, cancellationToken);

    public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken = default)
        => directory.RemoveMember(brainScopeId, accountId, cancellationToken);

    public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken = default)
        => directory.RemovePrincipalMember(brainScopeId, principalId, cancellationToken);
}

public sealed class IdentityNeuronState;

// The module's secrets@1 neuron. The vault port authorizes the stamped caller.
[Neuron("secrets")]
internal sealed class SecretsNeuron(ISecretVaultPort vault) : Neuron<SecretsNeuronState>, ISecrets
{
    public async Task<SecretRef> Set(string name, string label, string value, CancellationToken cancellationToken = default)
    {
        var result = await vault.Set(name, label, value, cancellationToken).ConfigureAwait(true);
        return result.Status == SecretVaultStatus.Ok && result.Secret is not null ? result.Secret : throw new SecretDenied(name);
    }

    public async Task Remove(string name, CancellationToken cancellationToken = default)
    {
        var status = await vault.Remove(name, cancellationToken).ConfigureAwait(true);
        if (status == SecretVaultStatus.Denied) { throw new SecretDenied(name); }
    }

    public async Task<string> Resolve(SecretRef secret, CancellationToken cancellationToken = default)
    {
        var result = await vault.Resolve(secret, cancellationToken).ConfigureAwait(true);
        if (result.Status == SecretVaultStatus.Ok && result.Value is not null) { return result.Value; }
        if (result.Status == SecretVaultStatus.Denied) { throw new SecretDenied(secret.Reference); }
        throw new SecretNotFound(secret.Reference);
    }

    public async Task<SealedSecret> Seal(string context, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken = default)
    {
        var sealedSecret = await vault.Seal(context, plaintext, cancellationToken).ConfigureAwait(true);
        return sealedSecret ?? throw new SecretDenied(context);
    }

    public async Task<byte[]> Open(string context, SealedSecret sealedSecret, CancellationToken cancellationToken = default)
    {
        var plaintext = await vault.Open(context, sealedSecret, cancellationToken).ConfigureAwait(true);
        return plaintext ?? throw new SecretDenied(context);
    }

    public async Task<SecretRef> Named(string name, CancellationToken cancellationToken = default)
    {
        var result = await vault.Named(name, cancellationToken).ConfigureAwait(true);
        return result.Status == SecretVaultStatus.Ok && result.Secret is not null ? result.Secret : throw new SecretDenied(name);
    }
}

public sealed class SecretsNeuronState;
