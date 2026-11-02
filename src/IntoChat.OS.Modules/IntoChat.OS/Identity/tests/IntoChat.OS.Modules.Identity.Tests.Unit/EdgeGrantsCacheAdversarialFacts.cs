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
    public async Task ConcurrentFriendRegistration_LostUpdatesStressTest()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brain = "stress-brain-lost-updates";
        cache.Invalidate(brain);

        const int concurrency = 40;
        var ownerApp = $"{brain}/apps/owner";

        // Concurrently register 40 distinct friends
        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            var friendApp = $"{brain}/apps/friend-{i}";
            cache.RegisterFriend(ownerApp, friendApp, $"scope.{i}");
        }, ct));

        await Task.WhenAll(tasks);

        var grants = await cache.GetGrantsAsync(brain, ct);

        // If AddGrant is thread-safe, all 40 friend grants must exist in cache
        var foundFriends = Enumerable.Range(0, concurrency)
            .Count(i => cache.IsFriend(ownerApp, $"{brain}/apps/friend-{i}", $"scope.{i}"));

        Assert.Equal(concurrency, foundFriends);
        Assert.Equal(concurrency, grants.Count);
    }

    [Fact]
    public async Task ConcurrentFriendRevocation_RaceConditionStressTest()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;

        for (var round = 0; round < 30; round++)
        {
            var brain = $"stress-brain-revocation-race-{round}";
            cache.Invalidate(brain);

            var ownerApp = $"{brain}/apps/owner";
            var victimFriend = $"{brain}/apps/victim-friend";

            cache.RegisterFriend(ownerApp, victimFriend, "sensitive.capability");

            const int addCount = 15;
            using var readyBarrier = new Barrier(addCount + 1);

            var revokeTask = Task.Run(() =>
            {
                readyBarrier.SignalAndWait(ct);
                cache.RevokeFriend(ownerApp, victimFriend, "sensitive.capability");
            }, ct);

            var addTasks = Enumerable.Range(0, addCount).Select(i => Task.Run(() =>
            {
                readyBarrier.SignalAndWait(ct);
                cache.RegisterFriend(ownerApp, $"{brain}/apps/friend-{i}", $"scope.{i}");
            }, ct));

            await Task.WhenAll(addTasks.Concat([revokeTask]));

            var isStillFriend = cache.IsFriend(ownerApp, victimFriend, "sensitive.capability");
            Assert.False(isStillFriend, $"CRITICAL: Revoked friend was resurrected in round {round} due to concurrent AddGrant overwriting RemoveGrant!");
        }
    }

    [Fact]
    public async Task CrossBrain_OwnersSharingWithAsync_FindsOwnerInDifferentBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var ownerBrain = "brain-owner";
        var friendBrain = "brain-friend";
        cache.Invalidate(ownerBrain);
        cache.Invalidate(friendBrain);

        var ownerApp = $"{ownerBrain}/apps/producer";
        var friendApp = $"{friendBrain}/apps/consumer";

        // Owner in brain-owner registers friend in brain-friend
        cache.RegisterFriend(ownerApp, friendApp, "data.stream");

        // Verify IsFriend works
        Assert.True(cache.IsFriend(ownerApp, friendApp, "data.stream"));

        // Now query from friend's perspective: who is sharing with friendApp?
        var owners = await cache.OwnersSharingWithAsync(friendApp, "data.stream", ct);

        // Should return ownerApp!
        Assert.NotEmpty(owners);
        Assert.Contains(ownerApp, owners);
    }

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

        // Verify CallBindings rejects unbound connection
        var callBindings = new ConnectionCallBindings(new NoConnectionBindings());
        var boundCall = new CallRequest
        {
            Caller = caller,
            TargetNeuron = "github/" + connectionKey,
            Operation = "Read",
        };
        Assert.False(await callBindings.IsBound(boundCall, brain, ct));
    }

    [Fact]
    public void RevokedFriend_DeniedByGrantRules()
    {
        var cache = EdgeGrantsCache.Current;
        var brain = "grantrules-revoke-brain";
        cache.Invalidate(brain);

        var ownerApp = $"{brain}/apps/owner";
        var friendApp = $"{brain}/apps/friend";
        var connectionKey = $"{brain}/connection/postgres/db";

        var ownerGrant = new Grant
        {
            WorkspaceId = brain,
            AppId = ownerApp,
            SemanticTypeId = "db.query",
            TargetNeuronKey = connectionKey,
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };

        cache.RegisterFriend(ownerApp, friendApp, "db.query");

        var friendCaller = CreateAppCaller(friendApp, brain);
        var request = new CallRequest
        {
            Caller = friendCaller,
            TargetNeuron = connectionKey,
            Operation = "Query",
            SemanticTypeIds = ["db.query"],
        };

        var decisionBefore = GrantRules.Evaluate(request, null, [ownerGrant]);
        Assert.NotNull(decisionBefore);
        Assert.True(decisionBefore!.Allowed);

        // Now revoke the friend
        cache.RevokeFriend(ownerApp, friendApp, "db.query");

        var decisionAfter = GrantRules.Evaluate(request, null, [ownerGrant]);
        Assert.NotNull(decisionAfter);
        Assert.False(decisionAfter!.Allowed, "Revoked friend MUST be denied by GrantRules!");
        Assert.Equal(CallDenial.MissingGrant, decisionAfter.Denial);
    }

    [Fact]
    public void MultiOwnerIsolation_FriendCannotAccessNonFriendOwnerCapabilities()
    {
        var cache = EdgeGrantsCache.Current;
        var brain = "isolation-brain";
        cache.Invalidate(brain);

        var owner1 = $"{brain}/apps/owner-1";
        var owner2 = $"{brain}/apps/owner-2";
        var friend = $"{brain}/apps/friend-of-1";

        var owner2Grant = new Grant
        {
            WorkspaceId = brain,
            AppId = owner2,
            SemanticTypeId = "secret.analytics",
            TargetNeuronKey = $"{brain}/connection/analytics/private",
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };

        // Only owner1 registers friend
        cache.RegisterFriend(owner1, friend, "chat.message");

        var friendCaller = CreateAppCaller(friend, brain);
        var request = new CallRequest
        {
            Caller = friendCaller,
            TargetNeuron = $"{brain}/connection/analytics/private",
            Operation = "GetReport",
            SemanticTypeIds = ["secret.analytics"],
        };

        var decision = GrantRules.Evaluate(request, null, [owner2Grant]);
        Assert.NotNull(decision);
        Assert.False(decision!.Allowed, "Friend of owner1 must NEVER have access to owner2's capabilities!");
        Assert.Equal(CallDenial.MissingGrant, decision.Denial);
    }

    [Fact]
    public async Task ConcurrentLostUpdates_AtScale_100Threads()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brain = "stress-brain-lost-updates-100";
        cache.Invalidate(brain);

        const int concurrency = 100;
        var ownerApp = $"{brain}/apps/owner";

        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            var friendApp = $"{brain}/apps/friend-{i}";
            cache.RegisterFriend(ownerApp, friendApp, $"scope.{i}");
        }, ct));

        await Task.WhenAll(tasks);

        var grants = await cache.GetGrantsAsync(brain, ct);

        var foundFriends = Enumerable.Range(0, concurrency)
            .Count(i => cache.IsFriend(ownerApp, $"{brain}/apps/friend-{i}", $"scope.{i}"));

        Assert.Equal(concurrency, foundFriends);
        Assert.Equal(concurrency, grants.Count);
    }

    [Fact]
    public async Task ConcurrentHighLoad_MixedOperations_NoDeadlockOrCorruption()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brain = "stress-mixed-ops-brain";
        cache.Invalidate(brain);

        const int workerCount = 80;
        var ownerApp = $"{brain}/apps/owner";
        using var startBarrier = new Barrier(workerCount);

        var tasks = Enumerable.Range(0, workerCount).Select(i => Task.Run(async () =>
        {
            startBarrier.SignalAndWait(ct);
            var friendApp = $"{brain}/apps/friend-{i % 10}";
            var connKey = $"{brain}/connection/postgres/db-{i % 5}";
            var caller = CreateAppCaller(friendApp, brain);

            switch (i % 5)
            {
                case 0:
                    cache.RegisterFriend(ownerApp, friendApp, $"scope.{i}");
                    break;
                case 1:
                    cache.BindFriendConnection(connKey, friendApp);
                    break;
                case 2:
                    _ = cache.IsFriend(ownerApp, friendApp, $"scope.{i}");
                    _ = await cache.IsConnectionBoundAsync(brain, caller, connKey, ct);
                    break;
                case 3:
                    _ = await cache.OwnersSharingWithAsync(friendApp, $"scope.{i}", ct);
                    break;
                case 4:
                    if (i % 2 == 0)
                    {
                        cache.RevokeFriend(ownerApp, friendApp, $"scope.{i}");
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

    [Fact]
    public async Task CrossBrain_OwnersSharingWithAsync_ComprehensiveFiltering()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;

        var targetBrain = "brain-target-consumer";
        var brainUs = "brain-us";
        var brainEu = "brain-eu";
        var brainAsia = "brain-asia";
        var brainLatam = "brain-latam";
        var brainAf = "brain-af";

        cache.Invalidate(targetBrain);
        cache.Invalidate(brainUs);
        cache.Invalidate(brainEu);
        cache.Invalidate(brainAsia);
        cache.Invalidate(brainLatam);
        cache.Invalidate(brainAf);

        var consumerApp = $"{targetBrain}/apps/consumer";
        var serviceUs = $"{brainUs}/apps/service-us";
        var serviceEu = $"{brainEu}/apps/service-eu";
        var serviceAsia = $"{brainAsia}/apps/service-asia";
        var serviceLatam = $"{brainLatam}/apps/service-latam";
        var serviceAf = $"{brainAf}/apps/service-af";

        // 1. service-us shares "payments.read"
        cache.RegisterFriend(serviceUs, consumerApp, "payments.read");

        // 2. service-eu shares "*" (wildcard)
        cache.RegisterFriend(serviceEu, consumerApp, "*");

        // 3. service-asia shares different capability "analytics.write"
        cache.RegisterFriend(serviceAsia, consumerApp, "analytics.write");

        // 4. service-latam was registered but then revoked
        cache.RegisterFriend(serviceLatam, consumerApp, "payments.read");
        cache.RevokeFriend(serviceLatam, consumerApp, "payments.read");

        // 5. service-af has an expired grant
        var expiredGrant = new Grant
        {
            Id = $"friend:{serviceAf}:{consumerApp}:payments.read",
            Owner = $"app:{serviceAf}",
            Grantee = $"app:{consumerApp}",
            Target = "payments.read",
            Scope = "payments.read",
            WorkspaceId = brainAf,
            AppId = consumerApp,
            SemanticTypeId = "payments.read",
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Expires = DateTimeOffset.UtcNow.AddHours(-1),
        };
        cache.AddGrant(brainAf, expiredGrant);

        // 6. Self-sharing grant (consumerApp -> consumerApp)
        cache.RegisterFriend(consumerApp, consumerApp, "payments.read");

        // Query: payments.read
        var paymentOwners = await cache.OwnersSharingWithAsync(consumerApp, "payments.read", ct);

        Assert.Equal(2, paymentOwners.Count);
        Assert.Contains(serviceUs, paymentOwners);
        Assert.Contains(serviceEu, paymentOwners);
        Assert.DoesNotContain(serviceAsia, paymentOwners);
        Assert.DoesNotContain(serviceLatam, paymentOwners);
        Assert.DoesNotContain(serviceAf, paymentOwners);
        Assert.DoesNotContain(consumerApp, paymentOwners);

        // Query: analytics.write
        var analyticsOwners = await cache.OwnersSharingWithAsync(consumerApp, "analytics.write", ct);
        Assert.Equal(2, analyticsOwners.Count);
        Assert.Contains(serviceAsia, analyticsOwners);
        Assert.Contains(serviceEu, analyticsOwners); // wildcard owner
        Assert.DoesNotContain(serviceUs, analyticsOwners);

        // Query with null scope: should return all active distinct external owners
        var allOwners = await cache.OwnersSharingWithAsync(consumerApp, null, ct);
        Assert.Equal(3, allOwners.Count);
        Assert.Contains(serviceUs, allOwners);
        Assert.Contains(serviceEu, allOwners);
        Assert.Contains(serviceAsia, allOwners);
    }
}

