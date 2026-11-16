using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Hosting;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class EdgeGrantsCacheAdversarialFacts
{
    private static CapabilityCaller CreateAppCaller(string appKey, string brain = "test-brain")
        => CapabilityCallers.Of(AppPrincipal.Id(appKey), "test-user", brain, CallerKind.App, appKey);

    [Fact]
    public async Task CrossBrain_ConnectionBinding_PreventsForeignBrainImpersonation()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brainAlpha = "brain-alpha";
        var brainBeta = "brain-beta";
        cache.Invalidate(brainAlpha);
        cache.Invalidate(brainBeta);

        var caller = CreateAppCaller($"{brainAlpha}/apps/app-1", brainAlpha);
        var alphaConnKey = $"{brainAlpha}/connection/postgres/main";
        var betaConnKey = $"{brainBeta}/connection/postgres/main";

        // Grant binding ONLY for brain-alpha connection
        cache.BindFriendConnection(alphaConnKey, $"{brainAlpha}/apps/app-1");

        // Verify alpha connection is bound
        var alphaBound = await cache.IsConnectionBoundAsync(brainAlpha, caller, alphaConnKey, ct);
        Assert.True(alphaBound);

        // Security check: caller should NOT be bound to beta connection with same relative name
        var betaBound = await cache.IsConnectionBoundAsync(brainAlpha, caller, betaConnKey, ct);
        Assert.False(betaBound, "SECURITY VULNERABILITY: Binding to brain-alpha connection allowed access to foreign brain-beta connection due to unqualified candidate match!");
    }

    [Fact]
    public async Task ConnectionUnbinding_ProperlyRevokesAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brain = "binding-revoke-brain";
        cache.Invalidate(brain);

        var friendApp = $"{brain}/apps/friend";
        var caller = CreateAppCaller(friendApp, brain);
        var connectionKey = $"{brain}/connection/github/shared";

        cache.BindFriendConnection(connectionKey, friendApp);
        Assert.True(await cache.IsConnectionBoundAsync(brain, caller, connectionKey, ct));

        // Unbind connection
        cache.UnbindFriendConnection(connectionKey, friendApp);

        // Verify no longer bound
        var isStillBound = await cache.IsConnectionBoundAsync(brain, caller, connectionKey, ct);
        Assert.False(isStillBound, "Unbound connection should not be reported as bound!");
    }

    [Fact]
    public async Task ConcurrentHighLoad_MixedOperations_NoDeadlockOrCorruption()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brain = "stress-mixed-ops-brain";
        cache.Invalidate(brain);

        const int workerCount = 80;
        var readyCount = 0;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, workerCount).Select(i => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref readyCount) == workerCount)
            {
                startGate.TrySetResult();
            }

            await startGate.Task.WaitAsync(ct);
            var friendApp = $"{brain}/apps/friend-{i % 10}";
            var connKey = $"{brain}/connection/postgres/db-{i % 5}";
            var caller = CreateAppCaller(friendApp, brain);

            switch (i % 4)
            {
                case 0:
                    cache.AddGrant(brain, Grant.Tuple($"brain:{brain}", $"app:{friendApp}", $"{brain}/apps/owner", $"scope.{i}"));
                    break;
                case 1:
                    cache.BindFriendConnection(connKey, friendApp);
                    break;
                case 2:
                    _ = await cache.IsGrantedAsync(brain, caller, $"scope.{i}", $"{brain}/apps/owner", ct);
                    _ = await cache.IsConnectionBoundAsync(brain, caller, connKey, ct);
                    break;
                case 3:
                    if (i % 2 == 0)
                    {
                        cache.RemoveGrant(brain, g => g.Scope == $"scope.{i}");
                    }
                    else
                    {
                        cache.UnbindFriendConnection(connKey, friendApp);
                    }
                    break;
            }
        }, ct));

        await Task.WhenAll(tasks);

        // State must remain queryable without throwing or deadlocking
        var remainingGrants = await cache.GetGrantsAsync(brain, ct);
        Assert.NotNull(remainingGrants);
    }

    [Fact]
    public async Task CrossBrain_ForeignConnectionImpersonation_AdversarialMatrix()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;

        var alphaBrain = "brain-matrix-alpha";
        var betaBrain = "brain-matrix-beta";
        var evilBrain = "brain-matrix-alpha-evil";
        cache.Invalidate(alphaBrain);
        cache.Invalidate(betaBrain);
        cache.Invalidate(evilBrain);

        var alphaApp1 = $"{alphaBrain}/apps/app-1";
        var alphaApp2 = $"{alphaBrain}/apps/app-2";
        var callerAlpha1 = CreateAppCaller(alphaApp1, alphaBrain);
        var callerAlpha2 = CreateAppCaller(alphaApp2, alphaBrain);

        var alphaConnKey = $"{alphaBrain}/connection/postgres/main";
        var betaConnKey = $"{betaBrain}/connection/postgres/main";
        var evilConnKey = $"{evilBrain}/connection/postgres/main";

        // Bind ONLY alphaApp1 to alphaConnKey
        cache.BindFriendConnection(alphaConnKey, alphaApp1);

        // 1. Legitimate binding succeeds
        Assert.True(await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, alphaConnKey, ct));

        // 2. Foreign brain with identical relative path MUST FAIL CLOSED
        var foreignAccess = await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, betaConnKey, ct);
        Assert.False(foreignAccess, "Foreign brain connection with identical relative path must be rejected!");

        // 3. Substring brain attack: brain-matrix-alpha-evil MUST FAIL CLOSED
        var substringAttack = await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, evilConnKey, ct);
        Assert.False(substringAttack, "Substring brain attack must be rejected!");

        // 4. Sibling app in same brain MUST FAIL CLOSED
        var siblingAppAccess = await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha2, alphaConnKey, ct);
        Assert.False(siblingAppAccess, "Sibling app in same brain without grant must be rejected!");

        // 5. Malformed connection keys MUST FAIL CLOSED without exceptions
        Assert.False(await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, "", ct));
        Assert.False(await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, "   ", ct));
        Assert.False(await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, "invalid", ct));
        Assert.False(await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, "not-a-connection/path", ct));
        Assert.False(await cache.IsConnectionBoundAsync(alphaBrain, null!, alphaConnKey, ct));

        // 6. Connector-prefixed target with foreign brain MUST FAIL CLOSED
        var foreignNested = $"postgres/{betaBrain}/connection/postgres/main";
        Assert.False(await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, foreignNested, ct));

        // 7. Connector-prefixed target with same brain MUST SUCCEED
        var sameNested = $"postgres/{alphaBrain}/connection/postgres/main";
        Assert.True(await cache.IsConnectionBoundAsync(alphaBrain, callerAlpha1, sameNested, ct));
    }
}
