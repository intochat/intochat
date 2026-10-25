using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Identity;

// A principal's password hash, on the machine (R62): the trust cluster never holds a credential.
[Alias("identity.principal-credential-grain")]
internal interface IPrincipalCredential : IGrainWithStringKey
{
    Task<string?> Hash();

    // False when a hash is already set: a username's first registrant keeps it.
    Task<bool> TryCreate(string hash);

    Task Rehash(string hash);

    // A registration the trust side refused leaves no credential behind (R67).
    Task Remove(string hash);
}

[GrainType("identity.principal-credential")]
internal sealed class PrincipalCredentialGrain(
    [PersistentState("credential", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<PrincipalCredentialState> store)
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

    public async Task Remove(string hash)
    {
        if (store.State.Hash != hash) { return; }
        await store.ClearStateAsync();
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
