using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

// Phase F: a connection is shared with a principal; the call gate admits exactly the app whose principal it was
// shared with, and ShareInto(brain, app) is the same sharing under its old name.
[Trait("host", "own")]
public sealed class ConnectionSharingFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task<ITestBrain> Start() => TestBrain.Create().WithModule<ExampleConnectionModule>().StartAsync(Ct);

    private static CallerContext AppCaller(Person person, string appKey) => new()
    {
        PrincipalId = AppPrincipal.Id(appKey),
        OnBehalfOf = person.PrincipalId,
        AccountId = person.AccountId,
        BrainId = person.BrainId,
        Kind = CallerKind.App,
        StampedBy = TrustedEdge.AppProxy,
        AppId = appKey,
    };

    [Fact]
    public async Task AConnectionSharedWithAnAppPrincipalIsVisibleToThatAppOnly()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        var key = ConnectionKeys.For(alice.BrainId, "example", "shared");
        var mail = $"{alice.BrainId}/apps/mail";
        var other = $"{alice.BrainId}/apps/other";
        using (alice.AsCaller()) { await brain.Get<IConnection>(key).Connect("api-key", "secret"); }

        await brain.Get<IConnection>(key).ShareWith(AppPrincipal.Id(mail));

        var bindings = brain.Services.GetRequiredService<IConnectionBindings>();
        Assert.True(await bindings.IsBound(AppCaller(alice, mail), key, Ct));
        Assert.False(await bindings.IsBound(AppCaller(alice, other), key, Ct));
        Assert.True(await brain.Get<IConnection>(key).IsSharedWith(AppPrincipal.Id(mail)));
        Assert.False(await brain.Get<IConnection>(key).IsSharedWith(AppPrincipal.Id(other)));

        await brain.Get<IConnection>(key).UnshareWith(AppPrincipal.Id(mail));
        Assert.False(await bindings.IsBound(AppCaller(alice, mail), key, Ct));
    }

    [Fact]
    public async Task ShareIntoIsTheSameSharingAsShareWithTheAppPrincipal()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        var key = ConnectionKeys.For(alice.BrainId, "example", "alias");
        var mail = $"{alice.BrainId}/apps/mail";
        using (alice.AsCaller()) { await brain.Get<IConnection>(key).Connect("api-key", "secret"); }

        await brain.Get<IConnection>(key).ShareInto(alice.BrainId, mail);

        Assert.True(await brain.Get<IConnection>(key).IsSharedWith(AppPrincipal.Id(mail)));
        await brain.Get<IConnection>(key).UnshareWith(AppPrincipal.Id(mail));
        Assert.False(await brain.Get<IConnection>(key).IsSharedInto(alice.BrainId, mail));
    }
}
