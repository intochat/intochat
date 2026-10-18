using System.Security.Cryptography;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Secrets;
using IntoChat.OS.Authority.Secrets;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Tests.Integration;

public sealed class SecretsFacts(AuthorityHost host)
{
    private const string Owner = "owner-1";
    private const string Canary = "canary-secret-7f3a91";

    [Fact]
    public void StoresCiphertextAndResolvesByReference()
    {
        var state = new SecretsState { Owner = Owner };
        var store = MasterKeyStore("first master key");

        var reference = store.Set(state, "api.key", "API key", Canary);

        Assert.DoesNotContain(Canary, state.Fields["api.key"].SealedSecret, StringComparison.Ordinal);
        Assert.Equal(Owner, reference.OwnerOf());
        Assert.Equal(Canary, store.Resolve(state, reference));
    }

    [Fact]
    public void RejectsReferenceForAnotherOwner()
    {
        var state = new SecretsState { Owner = Owner };
        var store = MasterKeyStore("first master key");
        store.Set(state, "api.key", "API key", Canary);

        Assert.Throws<InvalidOperationException>(() => store.Resolve(state, IntoChat.OS.Framework.SecretReferences.For("other", "api.key", "API key", true)));
    }

    [Fact]
    public void The_master_key_store_resolves_a_secret_it_has_set()
    {
        var state = new SecretsState { Owner = Owner };
        var store = MasterKeyStore("first master key");
        var reference = store.Set(state, "api.key", "API key", Canary);
        Assert.Equal(Canary, store.Resolve(state, reference));
    }

    [Fact]
    public void A_store_with_a_different_master_key_cannot_resolve_the_same_state()
    {
        var state = new SecretsState { Owner = Owner };
        var reference = MasterKeyStore("first master key").Set(state, "api.key", "API key", Canary);
        Assert.ThrowsAny<CryptographicException>(() => MasterKeyStore("different master key").Resolve(state, reference));
    }

    private static SecretsStore MasterKeyStore(string masterKey)
        => new(new MasterKeyWrapper(Options.Create(new MasterKeyOptions { MasterKey = masterKey })));

    [Fact]
    public async Task UserCannotResolveThroughGrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var secrets = brain.Platform.Secrets(Owner);
        var reference = await secrets.Set(UserCaller(), "api.key", "API key", Canary, ct);

        await Assert.ThrowsAsync<UntrustedCallerException>(() => secrets.Resolve(UserCaller(), reference, ct));
        Assert.Equal(Canary, await secrets.Resolve(PlatformCaller(), reference, ct));
    }

    private static CallerContext UserCaller() => new()
    {
        PrincipalId = Owner,
        AccountId = Owner,
        BrainId = Owner,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    };

    private static CallerContext PlatformCaller() => new()
    {
        PrincipalId = "test-app",
        AccountId = Owner,
        BrainId = Owner,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
        AppId = "test-app",
    };
}
