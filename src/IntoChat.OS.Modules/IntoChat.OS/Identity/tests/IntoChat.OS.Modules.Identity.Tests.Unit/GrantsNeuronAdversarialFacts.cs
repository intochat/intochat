using System.Collections;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class GrantsNeuronAdversarialFacts
{
    private sealed class MutableTestClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utc = initial;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan delta) => _utc += delta;
        public void Set(DateTimeOffset time) => _utc = time;
    }

    private sealed class CapturingClusterListeners : IClusterListeners
    {
        private readonly List<(string Listener, Type SignalType, Func<NeuronId, object, CancellationToken, Task> Handler)> _handlers = [];

        public Task<IAsyncDisposable> Listen<TSignal>(string listener, Func<NeuronId, TSignal, CancellationToken, Task> handler) where TSignal : Signal
        {
            _handlers.Add((listener, typeof(TSignal), (publisher, signal, ct) => handler(publisher, (TSignal)signal, ct)));
            return Task.FromResult<IAsyncDisposable>(new DummyDisposable());
        }

        public async Task EmitAsync<TSignal>(NeuronId publisher, TSignal signal, CancellationToken ct = default) where TSignal : Signal
        {
            foreach (var (_, type, handler) in _handlers)
            {
                if (type == typeof(TSignal))
                {
                    await handler(publisher, signal, ct);
                }
            }
        }

        private sealed class DummyDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestNeuronHost(Func<RuntimeInvocation, object?> handler) : INeuronHost, INeuronHostCatalog, IGrainWithStringKey
    {
        public Task<string?> HostType(string neuronType) => Task.FromResult<string?>("neuron.host");

        public Task<bool> IsInterleaved(RuntimeInvocation call) => Task.FromResult(false);

        public Task<JsonElement?> Invoke(RuntimeInvocation call, CancellationToken cancellationToken = default)
        {
            var result = handler(call);
            var json = JsonSerializer.SerializeToElement(result);
            return Task.FromResult<JsonElement?>(json);
        }

        public Task<JsonElement?> InvokeInterleaved(RuntimeInvocation call, CancellationToken cancellationToken = default) => Invoke(call, cancellationToken);
        public Task Post(RuntimeInvocation call) => Task.CompletedTask;
        public Task PostInterleaved(RuntimeInvocation call) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> Stream(RuntimeInvocation call, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<JsonElement?> InspectState(CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);

        public Task<DigitalBrain.Runtime.Orleans.Synapses.GateVerdict> Gate(string signalAlias, JsonElement signal, Guid attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Committed(string signalAlias, JsonElement signal, JsonElement next, Guid ticket, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Aborted(string signalAlias, JsonElement signal, Guid ticket, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Reload() => Task.CompletedTask;
    }

    private sealed class TestGrainFactory(Func<Type, string, IGrain> hostFactory, INeuronHostCatalog catalog) : IGrainFactory
    {
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => hostFactory(grainInterfaceType, grainPrimaryKey);
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey, string _) => hostFactory(grainInterfaceType, grainPrimaryKey);
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotImplementedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string _) => throw new NotImplementedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotImplementedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string _) => throw new NotImplementedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey) => throw new NotImplementedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey, string _) => throw new NotImplementedException();
        public IAddressable GetGrain(GrainId grainId) => catalog;
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => catalog;

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string grainPrimaryKey, string? _)
        {
            if (typeof(TGrainInterface) == typeof(INeuronHostCatalog))
            {
                return (TGrainInterface)catalog;
            }
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainPrimaryKey);
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid grainPrimaryKey, string? _)
            => throw new NotImplementedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long grainPrimaryKey, string? _)
            => throw new NotImplementedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid grainPrimaryKey, string _1, string? _2)
            => throw new NotImplementedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long grainPrimaryKey, string _1, string? _2)
            => throw new NotImplementedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => (TGrainInterface)catalog;
        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotImplementedException();
        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task MonotonicVersionProgression_SequentialMutationsStrictlyIncrementVersion()
    {
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        var brain = "monotonic-brain";
        var id = new NeuronId(new BrainId(brain), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey);
        var grantsNeuron = os.Get<IGrantsNeuron>(id);

        var initialVersion = await grantsNeuron.GetVersion();
        Assert.Equal(0, initialVersion);

        long lastVersion = initialVersion;

        // Step 1: 25 sequential Grant operations with unique IDs
        for (var i = 1; i <= 25; i++)
        {
            var grant = new Grant
            {
                Id = $"grant-{i}",
                Owner = "app:local/apps/owner",
                Grantee = $"app:local/apps/grantee-{i}",
                Target = $"resource/{i}",
                Scope = "read",
                WorkspaceId = brain,
                AppId = $"local/apps/grantee-{i}",
                SemanticTypeId = "read",
                Mode = GrantMode.Always,
                GrantedAt = DateTimeOffset.UtcNow,
            };

            await grantsNeuron.Grant(grant);
            var currentVersion = await grantsNeuron.GetVersion();
            Assert.Equal(lastVersion + 1, currentVersion);
            Assert.True(currentVersion > lastVersion, $"Version must advance monotonically: {currentVersion} > {lastVersion}");
            lastVersion = currentVersion;
        }
        Assert.Equal(25, lastVersion);

        // Step 2: 10 updates to existing grants (same Id, updated Scope)
        for (var i = 1; i <= 10; i++)
        {
            var updatedGrant = new Grant
            {
                Id = $"grant-{i}",
                Owner = "app:local/apps/owner",
                Grantee = $"app:local/apps/grantee-{i}",
                Target = $"resource/{i}",
                Scope = "write", // updated scope
                WorkspaceId = brain,
                AppId = $"local/apps/grantee-{i}",
                SemanticTypeId = "write",
                Mode = GrantMode.Always,
                GrantedAt = DateTimeOffset.UtcNow,
            };

            await grantsNeuron.Grant(updatedGrant);
            var currentVersion = await grantsNeuron.GetVersion();
            Assert.Equal(lastVersion + 1, currentVersion);
            Assert.True(currentVersion > lastVersion, $"Update must advance version: {currentVersion} > {lastVersion}");
            lastVersion = currentVersion;
        }
        Assert.Equal(35, lastVersion);

        // Step 3: Non-mutating Revoke calls (non-existent ID, whitespace, empty) MUST NOT bump version
        await grantsNeuron.Revoke("non-existent-grant-id");
        Assert.Equal(lastVersion, await grantsNeuron.GetVersion());

        await grantsNeuron.Revoke("");
        Assert.Equal(lastVersion, await grantsNeuron.GetVersion());

        await grantsNeuron.Revoke("   ");
        Assert.Equal(lastVersion, await grantsNeuron.GetVersion());

        // Step 4: 15 sequential Revoke calls for existing grants
        for (var i = 11; i <= 25; i++)
        {
            await grantsNeuron.Revoke($"grant-{i}");
            var currentVersion = await grantsNeuron.GetVersion();
            Assert.Equal(lastVersion + 1, currentVersion);
            Assert.True(currentVersion > lastVersion, $"Revoke must advance version: {currentVersion} > {lastVersion}");
            lastVersion = currentVersion;
        }
        Assert.Equal(50, lastVersion);

        // Step 5: Duplicate revoke of already-revoked grant MUST NOT bump version
        for (var i = 11; i <= 25; i++)
        {
            await grantsNeuron.Revoke($"grant-{i}");
            Assert.Equal(lastVersion, await grantsNeuron.GetVersion());
        }

        // Verify state consistency: exactly 10 active grants remain
        var activeGrants = await grantsNeuron.List();
        Assert.Equal(10, activeGrants.Length);
        Assert.All(activeGrants, g => Assert.Equal("write", g.Scope));
    }

    [Fact]
    public async Task GrantsNeuron_FiltersExpiredAndRevokedGrantsFromList()
    {
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        var brain = "filter-brain";
        var id = new NeuronId(new BrainId(brain), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey);
        var grantsNeuron = os.Get<IGrantsNeuron>(id);

        var now = DateTimeOffset.UtcNow;

        // Grant 1: Active, null expiration
        await grantsNeuron.Grant(new Grant
        {
            Id = "grant-permanent",
            Owner = "owner",
            Grantee = "grantee",
            Target = "target-1",
            Expires = null,
            Revoked = false,
        });

        // Grant 2: Active, future expiration
        await grantsNeuron.Grant(new Grant
        {
            Id = "grant-future",
            Owner = "owner",
            Grantee = "grantee",
            Target = "target-2",
            Expires = now + TimeSpan.FromHours(2),
            Revoked = false,
        });

        // Grant 3: Expired in the past
        await grantsNeuron.Grant(new Grant
        {
            Id = "grant-past",
            Owner = "owner",
            Grantee = "grantee",
            Target = "target-3",
            Expires = now - TimeSpan.FromMinutes(5),
            Revoked = false,
        });

        // Grant 4: Flagged as Revoked
        await grantsNeuron.Grant(new Grant
        {
            Id = "grant-flagged-revoked",
            Owner = "owner",
            Grantee = "grantee",
            Target = "target-4",
            Expires = null,
            Revoked = true,
        });

        var active = await grantsNeuron.List();
        Assert.Equal(2, active.Length);
        Assert.Contains(active, g => g.Id == "grant-permanent");
        Assert.Contains(active, g => g.Id == "grant-future");
        Assert.DoesNotContain(active, g => g.Id == "grant-past");
        Assert.DoesNotContain(active, g => g.Id == "grant-flagged-revoked");
    }

    [Fact]
    public async Task EdgeGrantsCache_InvalidatesOnGrantsChangedSignal()
    {
        var original = EdgeGrantsCache.Current;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var listeners = new CapturingClusterListeners();
            var cache = new EdgeGrantsCache(services: null, listeners: listeners);

            var brainA = "brain-alpha";
            var brainB = "brain-beta";
            var brainC = "brain-gamma";

            cache.AddGrant(brainA, new Grant { Id = "g-a", Target = "res-a", Scope = "read" });
            cache.AddGrant(brainB, new Grant { Id = "g-b", Target = "res-b", Scope = "read" });
            cache.AddGrant(brainC, new Grant { Id = "g-c", Target = "res-c", Scope = "read" });

            // Verify preloaded state
            Assert.NotEmpty(await cache.GetGrantsAsync(brainA, ct));
            Assert.NotEmpty(await cache.GetGrantsAsync(brainB, ct));
            Assert.NotEmpty(await cache.GetGrantsAsync(brainC, ct));

            // Signal 1: GrantsChanged for brain-alpha
            var pubA = new NeuronId(new BrainId(brainA), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey);
            await listeners.EmitAsync(pubA, new GrantsChanged(Version: 2), ct);

            // brain-alpha must be invalidated from cache; brain-beta and brain-gamma must remain cached
            Assert.Empty(await cache.GetGrantsAsync(brainA, ct)); // empty because services is null and cache was removed
            Assert.NotEmpty(await cache.GetGrantsAsync(brainB, ct));
            Assert.NotEmpty(await cache.GetGrantsAsync(brainC, ct));

            // Signal 2: GrantsChanged for brain-beta
            var pubB = new NeuronId(new BrainId(brainB), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey);
            await listeners.EmitAsync(pubB, new GrantsChanged(Version: 2), ct);

            Assert.Empty(await cache.GetGrantsAsync(brainB, ct));
            Assert.NotEmpty(await cache.GetGrantsAsync(brainC, ct));

            // Signal 3: Empty publisher brain does not crash or invalidate brain-gamma
            var pubEmpty = new NeuronId(new BrainId("unrelated"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey);
            await listeners.EmitAsync(pubEmpty, new GrantsChanged(Version: 1), ct);
            Assert.NotEmpty(await cache.GetGrantsAsync(brainC, ct));
        }
        finally
        {
            EdgeGrantsCache.Current = original;
        }
    }

    [Fact]
    public async Task EdgeGrantsCache_30SecondTtlFallback_WhenBroadcastIsDropped()
    {
        var original = EdgeGrantsCache.Current;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var startTime = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
            var clock = new MutableTestClock(startTime);
            var brain = "dropped-broadcast-brain";

            var remoteGrants = new List<Grant>
            {
                new() { Id = "g-v1", Owner = "owner", Grantee = "grantee", Target = "res-1", Scope = "read" }
            };
            long remoteVersion = 1;

            var host = new TestNeuronHost(call =>
            {
                if (call.Method == "List") { return remoteGrants.ToArray(); }
                if (call.Method == "GetVersion") { return remoteVersion; }
                throw new NotSupportedException($"Unexpected call: {call.Method}");
            });

            var grainFactory = new TestGrainFactory((_, _) => host, host);
            var services = new ServiceCollection()
                .AddSingleton<IGrainFactory>(grainFactory)
                .BuildServiceProvider();

            // EdgeGrantsCache with NO cluster listeners registered (simulating dropped broadcast / disconnected cluster)
            var cache = new EdgeGrantsCache(services: services, listeners: null, clock: clock);

            // Fetch 1 at t = 0s: fetches from remote neuron, caches with Version 1
            var result1 = await cache.GetGrantsAsync(brain, ct);
            Assert.Single(result1);
            Assert.Equal("g-v1", result1[0].Id);

            // Simulate remote neuron state change at t = 10s: Grant-2 added, Version bumped to 2
            // Broadcast signal is DROPPED (never emitted to cache)
            remoteGrants.Add(new Grant { Id = "g-v2", Owner = "owner", Grantee = "grantee", Target = "res-2", Scope = "write" });
            remoteVersion = 2;

            clock.Advance(TimeSpan.FromSeconds(10)); // t = 10s (< 30s TTL)
            var resultAt10s = await cache.GetGrantsAsync(brain, ct);
            Assert.Single(resultAt10s); // Still returns cached v1 grants!
            Assert.Equal("g-v1", resultAt10s[0].Id);

            clock.Advance(TimeSpan.FromSeconds(15)); // t = 25s (< 30s TTL)
            var resultAt25s = await cache.GetGrantsAsync(brain, ct);
            Assert.Single(resultAt25s); // Still returns cached v1 grants!
            Assert.Equal("g-v1", resultAt25s[0].Id);

            clock.Advance(TimeSpan.FromSeconds(4)); // t = 29s (< 30s TTL)
            var resultAt29s = await cache.GetGrantsAsync(brain, ct);
            Assert.Single(resultAt29s); // Still cached v1

            // Advance clock past the 30-second TTL threshold: t = 31s (> 30s)
            clock.Advance(TimeSpan.FromSeconds(2)); // t = 31s
            var resultAt31s = await cache.GetGrantsAsync(brain, ct);

            // 30s TTL expired! Cache refetches from remote neuron and picks up v2 grants!
            Assert.Equal(2, resultAt31s.Count);
            Assert.Contains(resultAt31s, g => g.Id == "g-v1");
            Assert.Contains(resultAt31s, g => g.Id == "g-v2");

            // Subsequent call at t = 35s hits the newly refreshed cache
            clock.Advance(TimeSpan.FromSeconds(4)); // t = 35s (cached at 31s, elapsed 4s < 30s)
            var resultAt35s = await cache.GetGrantsAsync(brain, ct);
            Assert.Equal(2, resultAt35s.Count);
        }
        finally
        {
            EdgeGrantsCache.Current = original;
        }
    }

    [Fact]
    public async Task EdgeGrantsCache_GracefulFallbackToStaleCache_OnSiloException()
    {
        var original = EdgeGrantsCache.Current;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var startTime = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
            var clock = new MutableTestClock(startTime);
            var brain = "silo-failure-brain";

            var shouldThrow = false;
            var remoteGrants = new List<Grant>
            {
                new() { Id = "g-initial", Target = "res-1", Scope = "read" }
            };

            var host = new TestNeuronHost(call =>
            {
                if (shouldThrow) { throw new TimeoutException("Silo unresponsive / partitioned"); }
                if (call.Method == "List") { return remoteGrants.ToArray(); }
                if (call.Method == "GetVersion") { return 1L; }
                throw new NotSupportedException();
            });

            var grainFactory = new TestGrainFactory((_, _) => host, host);
            var services = new ServiceCollection()
                .AddSingleton<IGrainFactory>(grainFactory)
                .BuildServiceProvider();

            var cache = new EdgeGrantsCache(services: services, listeners: null, clock: clock);

            // Fetch initial grants at t = 0s
            var initial = await cache.GetGrantsAsync(brain, ct);
            Assert.Single(initial);
            Assert.Equal("g-initial", initial[0].Id);

            // Silo goes down!
            shouldThrow = true;

            // Advance clock past 30 seconds TTL
            clock.Advance(TimeSpan.FromSeconds(35));

            // When TTL expires, remote call throws TimeoutException.
            // Cache MUST catch it, log swallowed warning, and gracefully fallback to stale cached grants!
            var fallback = await cache.GetGrantsAsync(brain, ct);
            Assert.Single(fallback);
            Assert.Equal("g-initial", fallback[0].Id);
        }
        finally
        {
            EdgeGrantsCache.Current = original;
        }
    }

    [Fact]
    public async Task StaticIsolation_NoStaticMemoryCollectionsInEdgeGrantsCache()
    {
        var original = EdgeGrantsCache.Current;
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Check 1: EdgeGrantsCache static fields
            var edgeFields = typeof(EdgeGrantsCache).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in edgeFields)
            {
                var type = field.FieldType;
                if (type == typeof(TimeSpan) || type == typeof(EdgeGrantsCache)) { continue; }
                Assert.False(typeof(IEnumerable).IsAssignableFrom(type),
                    $"EdgeGrantsCache has static collection field '{field.Name}' of type '{type.Name}'. Open-ended caches must be instance fields.");
            }

            // Check 2: Instance isolation verification
            var cache1 = new EdgeGrantsCache();
            var cache2 = new EdgeGrantsCache();

            cache1.AddGrant("isolation-brain", new Grant { Id = "c1-grant", Target = "c1-target" });

            var c1List = await cache1.GetGrantsAsync("isolation-brain", ct);
            var c2List = await cache2.GetGrantsAsync("isolation-brain", ct);

            Assert.Single(c1List);
            Assert.Empty(c2List); // cache2 must be completely isolated and unaffected by cache1

            cache1.Clear();
            Assert.Empty(await cache1.GetGrantsAsync("isolation-brain", ct));
        }
        finally
        {
            EdgeGrantsCache.Current = original;
        }
    }

    [Fact]
    public async Task EdgeGrantsCache_IsConnectionBoundAsync_AdversarialTargetMatching()
    {
        var original = EdgeGrantsCache.Current;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var cache = new EdgeGrantsCache();
            var brain = "binding-adversarial-brain";

            var caller = CapabilityCallers.Of(AppPrincipal.Id("local/apps/client"), "user-1", brain, CallerKind.App, "local/apps/client");

            // Grant with neuron:// prefix on target and app: prefix on grantee
            var grant = new Grant
            {
                Id = "binding-1",
                Owner = $"brain:{brain}",
                Grantee = "app:local/apps/client",
                Target = $"neuron://{brain}/connection/storage/files",
                Scope = "use-via:local/apps/client",
                WorkspaceId = brain,
                AppId = "local/apps/client",
                SemanticTypeId = "use-via:local/apps/client",
                TargetNeuronKey = $"{brain}/connection/storage/files",
                Mode = GrantMode.Always,
                GrantedAt = DateTimeOffset.UtcNow,
            };
            cache.AddGrant(brain, grant);

            // Test with exact neuron:// target
            Assert.True(await cache.IsConnectionBoundAsync(brain, caller, $"neuron://{brain}/connection/storage/files", ct));

            // Test with plain {brain}/connection target
            Assert.True(await cache.IsConnectionBoundAsync(brain, caller, $"{brain}/connection/storage/files", ct));

            // Test with unqualified subpath connection/storage/files
            Assert.True(await cache.IsConnectionBoundAsync(brain, caller, "connection/storage/files", ct));

            // Test with wrong caller app
            var stranger = CapabilityCallers.Of(AppPrincipal.Id("local/apps/stranger"), "user-2", brain, CallerKind.App, "local/apps/stranger");
            Assert.False(await cache.IsConnectionBoundAsync(brain, stranger, $"{brain}/connection/storage/files", ct));

            // Test with mismatching connection
            Assert.False(await cache.IsConnectionBoundAsync(brain, caller, $"{brain}/connection/database/sql", ct));
        }
        finally
        {
            EdgeGrantsCache.Current = original;
        }
    }
}
