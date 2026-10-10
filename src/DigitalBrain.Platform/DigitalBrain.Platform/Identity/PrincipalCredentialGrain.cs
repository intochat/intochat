using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Identity;

// A principal's password hash, on the machine (R62): the trust cluster never holds a credential.
internal interface IPrincipalCredential : IGrainWithStringKey
{
    Task<string?> Hash();

    // False when a hash is already set: a username's first registrant keeps it.
    Task<bool> TryCreate(string hash);

    Task Rehash(string hash);
}

[GrainType("identity.principal-credential")]
internal sealed class PrincipalCredentialGrain(
    [PersistentState("credential", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<PrincipalCredentialState> store)
    : Grain, IPrincipalCredential
{
    public Task<string?> Hash() => Task.FromResult(store.State.Hash);

    public async Task<bool> TryCreate(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (store.State.Hash is not null) { return false; }
        store.State.Hash = hash;
        await store.WriteStateAsync();
        return true;
    }

    public async Task Rehash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (store.State.Hash is null) { return; }
        store.State.Hash = hash;
        await store.WriteStateAsync();
    }
}

[GenerateSerializer, Alias("identity.principal-credential-state")]
internal sealed class PrincipalCredentialState
{
    [Id(0)] public string? Hash { get; set; }
}
