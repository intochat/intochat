using System.Security.Cryptography;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Secrets;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task TheRedactorsValueSourceReturnsOnlyTheStampedCallersVault()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        const string Other = "other-canary-5d1e8b";
        var owner = brain.Owner;
        var member = await owner.AddAccountMemberAsync(brain, "vault-member");
        static CallerContext As(Person person) => new()
        {
            PrincipalId = person.PrincipalId,
            AccountId = person.AccountId,
            BrainId = person.BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        };
        await brain.Platform.Secrets(owner.PrincipalId).Set(As(owner), "api.key", "API key", Canary, ct);
        await brain.Platform.Secrets(member.PrincipalId).Set(As(member), "api.key", "API key", Other, ct);
        var source = brain.Services.GetRequiredService<ISecretValueSource>();

        using (owner.AsCaller())
        {
            var values = await source.CurrentValues(ct);
            Assert.Contains(Canary, values);
            Assert.DoesNotContain(Other, values);
        }
    }

    [Fact]
    public async Task AForeignCallerIsRefusedTheVaultValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var owner = brain.Owner;
        var member = await owner.AddAccountMemberAsync(brain, "vault-intruder");
        await brain.Platform.Secrets(owner.PrincipalId).Set(new CallerContext
        {
            PrincipalId = owner.PrincipalId,
            AccountId = owner.AccountId,
            BrainId = owner.BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        }, "gate.key", "Gate key", "gate-canary-91c4d7", ct);
        var vault = brain.Platform.SecretValues(owner.PrincipalId);

        using (member.AsCaller())
        {
            await Assert.ThrowsAsync<UntrustedCallerException>(vault.Values);
        }
        using (owner.AsCaller())
        {
            Assert.Contains("gate-canary-91c4d7", await vault.Values());
        }
    }

    [Fact]
    public void AVaultListsItsPlaintextValuesForMaskingOnly()
    {
        var state = new SecretsState { Owner = Owner };
        var store = MasterKeyStore("first master key");
        Assert.Empty(store.Values(state));
        store.Set(state, "api.key", "API key", Canary);
        Assert.Equal([Canary], store.Values(state));
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
