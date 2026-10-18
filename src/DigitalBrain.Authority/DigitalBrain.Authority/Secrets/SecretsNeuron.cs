using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Secrets;

namespace DigitalBrain.Authority.Secrets;

[GrainType("vault")]
internal sealed class SecretsNeuron : Grain, ISecrets
{
    private readonly IPersistentState<SecretsState> _state;
    private readonly SecretsStore _secrets;

    public SecretsNeuron(
        [PersistentState("vault", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<SecretsState> state,
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
        await _state.WriteStateAsync();
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
            await _state.WriteStateAsync();
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
}
