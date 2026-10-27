using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Connections.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

public sealed class BrainOwnerDispatchFacts(AuthorityHost host)
{
    [Fact]
    public async Task PersistedOwnerRunsWithoutPlatformPrivilegeAndOuterContextIsRestored()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.Brain.PersonAsync(AuthorityHost.Unique("dispatch"), ct);
        var dispatch = host.Brain.Services.GetRequiredService<IBrainOwnerDispatch>();
        using var outer = host.Brain.Owner.AsCaller();
        var outerCaller = CallerContextStamper.Require();
        var outerTrace = TraceContext.Current;
        Assert.True(await dispatch.RunAsync(person.BrainId, new(person.Key("delivery")), async () =>
        {
            await Task.Yield();
            var caller = CallerContextStamper.Require();
            Assert.Equal(person.PrincipalId, caller.PrincipalId);
            Assert.Equal(person.AccountId, caller.AccountId);
            Assert.Equal(person.BrainId, caller.BrainId);
            Assert.Equal(CallerKind.User, caller.Kind);
            Assert.Equal(TrustedEdge.AuthenticatedHttp, caller.StampedBy);
            Assert.Equal(person.AccountId, TraceContext.Current!.PayerAccountId);
            Assert.Equal(person.BrainId, TraceContext.Current.BrainId);
        }, cancellationToken: ct));
        Assert.Equal(outerCaller, CallerContextStamper.Require());
        Assert.Equal(outerTrace, TraceContext.Current);
        await Assert.ThrowsAsync<IOException>(() => dispatch.RunAsync(person.BrainId, new(person.Key("delivery")),
            () => Task.FromException(new IOException("Fixture callback failure.")), cancellationToken: ct));
        Assert.Equal(outerCaller, CallerContextStamper.Require());
        Assert.Equal(outerTrace, TraceContext.Current);
    }

    [Fact]
    public async Task MissingOwnerForgedOriginAndForeignTraceNeverRunTheCallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.Brain.PersonAsync(AuthorityHost.Unique("dispatch-guards"), ct);
        var dispatch = host.Brain.Services.GetRequiredService<IBrainOwnerDispatch>();
        var ran = false;
        Task Work() { ran = true; return Task.CompletedTask; }
        Assert.False(await dispatch.RunAsync("missing-owner", new("missing-owner/delivery"), Work, cancellationToken: ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatch.RunAsync(person.BrainId, new("other/delivery"), Work, cancellationToken: ct));
        var forged = TraceContext.Begin("other-account", person.BrainId, new(person.Key("delivery")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatch.RunAsync(person.BrainId, new(person.Key("delivery")), Work, forged, ct));
        Assert.False(ran);
    }
}
