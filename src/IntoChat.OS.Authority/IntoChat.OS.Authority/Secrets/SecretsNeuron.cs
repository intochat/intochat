using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Platform.Secrets;

namespace IntoChat.OS.Authority.Secrets;

[GrainType("vault")]
internal sealed class SecretsNeuron : Grain, ISecrets, ISecretVaultValues
{
    private readonly IPersistentState<SecretsState> _state;
    private readonly SecretsStore _secrets;

    public SecretsNeuron(
        [PersistentState("vault", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<SecretsState> state,
        IKeyWrapper keys)
    {
        _state = state;
        _secrets = new SecretsStore(keys);
    }

    public async Task<SecretRef> Set(CallerContext caller, string name, string label, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Kind == CallerKind.User && caller.PrincipalId != this.GetPrimaryKeyString())
        {
            throw new InvalidOperationException("Only the owner can store a secret.");
        }

        _state.State.Owner = this.GetPrimaryKeyString();
        var reference = _secrets.Set(_state.State, name, label, value);
        await _state.WriteStateAsync(cancellationToken);
        return reference;
    }

    public async Task Remove(CallerContext caller, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var isOwner = caller.Kind == CallerKind.User && caller.PrincipalId == this.GetPrimaryKeyString();
        var isPlatform = caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform;
        if (!isOwner && !isPlatform)
        {
            throw new InvalidOperationException("Only the owner or trusted platform code can remove a secret.");
        }

        if (_state.State.Fields.Remove(name))
        {
            await _state.WriteStateAsync(cancellationToken);
        }
    }

    public Task<string> Resolve(CallerContext caller, SecretRef secret, CancellationToken cancellationToken = default)
    {
        if (caller is null || caller.Kind == CallerKind.User
            || caller.StampedBy is not (TrustedEdge.AppProxy or TrustedEdge.Platform)
            || string.IsNullOrEmpty(caller.AppId))
        {
            throw new UntrustedCallerException("Only a trusted outbound call can resolve a secret.");
        }

        _state.State.Owner = this.GetPrimaryKeyString();
        return Task.FromResult(_secrets.Resolve(_state.State, secret));
    }

    // Plaintext leaves only to the stamped owner of this vault or to the Authority's own platform stamp.
    public Task<string[]> Values()
    {
        if (!CallerContextStamper.TryGet(out var caller)
            || !(caller.PrincipalId == this.GetPrimaryKeyString() || (caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform)))
        {
            throw new UntrustedCallerException("Only the vault's owner can list its values.");
        }

        return Task.FromResult(_secrets.Values(_state.State));
    }
}
