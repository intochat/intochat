using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.Aspire;
using IntoChat.Aspire.Bridge;
using IntoChat.Aspire.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

public sealed class AspireBridgeAndExecuteFacts
{
    private static readonly AspireCommand RestartCmd = new("resource-restart", "Restart", "Restart resource", true, null);
    private static readonly AspireCommand ResetCmd = new("reset-state", "Reset Local State", "Wipe local state", true, "This deletes all local state.");
    private static readonly AspireCommand DisabledCmd = new("resource-start", "Start", null, false, null);

    private static BridgeSnapshot Snapshot(
        string name,
        string? instanceId = null,
        string state = "Running",
        string? health = "Healthy",
        AspireCommand[]? commands = null,
        string[]? urls = null,
        bool removed = false)
        => new(name, instanceId ?? name, "Container", null, state, health, urls ?? [], null, null, commands ?? [RestartCmd], removed);

    [Fact]
    public void ContractRecordsRoundTripAndOmitNullOrDefaultFields()
    {
        var cmd = new AspireCommand("resource-restart", "Restart", null, true, null);
        var inst = new AspireInstanceView("web-searxng", "Running", "Healthy", ["http://localhost:8080"], null, null, [cmd]);
        var res = new AspireResource("web-searxng", "Container", "web", [inst]);
        var all = new AspireResources(true, [res]);
        var result = new AspireCommandResult("web-searxng", "resource-restart", true);
        var snap = Snapshot("web-searxng");

        Assert.Equivalent(all, RoundTrip(all), strict: true);
        Assert.Equivalent(result, RoundTrip(result), strict: true);
        Assert.Equivalent(snap, RoundTrip(snap), strict: true);

        var resultJson = JsonSerializer.Serialize(result, RuntimeProtocol.OptionsFor(typeof(AspireCommandResult)));
        Assert.DoesNotContain("\"Reason\"", resultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Message\"", resultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Confirmation\"", resultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Known\"", resultJson, StringComparison.OrdinalIgnoreCase);

        var snapJson = JsonSerializer.Serialize(snap, RuntimeProtocol.OptionsFor(typeof(BridgeSnapshot)));
        Assert.DoesNotContain("\"Removed\"", snapJson, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("aspire.resource.changed", typeof(AspireResourceChanged).GetCustomAttribute<SignalAttribute>()?.Alias);
        Assert.Equal("aspire.command.requested", typeof(AspireCommandRequested).GetCustomAttribute<SignalAttribute>()?.Alias);
        Assert.Equal("aspire.command.executed", typeof(AspireCommandExecuted).GetCustomAttribute<SignalAttribute>()?.Alias);
        Assert.Equal("aspire.command.refused", typeof(AspireCommandRefused).GetCustomAttribute<SignalAttribute>()?.Alias);

        var desc = typeof(IAspire).GetMethod(nameof(IAspire.Execute))?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.False(string.IsNullOrWhiteSpace(desc));
        Assert.Contains("approval", desc!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kernel", desc!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refused", desc!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostStoreGroupsInstancesSuppressesNoiseAndHandlesRemovalAndDisconnect()
    {
        var store = new AspireHostStore();
        Assert.False(store.Connected);
        Assert.False(store.Read().Connected);

        store.SetConnected(true);
        Assert.Single(store.Apply(Snapshot("worker", "worker-1", "Running", "Healthy")));
        Assert.Single(store.Apply(Snapshot("worker", "worker-2", "Running", "Healthy")));

        // Identical or URL-only snapshot publishes nothing.
        Assert.Empty(store.Apply(Snapshot("worker", "worker-1", "Running", "Healthy")));
        Assert.Empty(store.Apply(Snapshot("worker", "worker-1", "Running", "Healthy", urls: ["http://localhost:9000"])));

        // Flapping health publishes once per real change; identical publishes 0.
        var published = 0;
        for (var i = 0; i < 20; i++)
        {
            published += store.Apply(Snapshot("worker", "worker-1", "Running", i % 2 == 0 ? "Unhealthy" : "Healthy")).Count;
        }
        Assert.Equal(20, published);

        var grouped = Assert.Single(store.Read().Items);
        Assert.Equal("worker", grouped.Name);
        Assert.Equal(["worker-1", "worker-2"], grouped.Instances.Select(i => i.Id).ToArray());

        // Removed drops the instance.
        Assert.Single(store.Apply(Snapshot("worker", "worker-2", removed: true)));
        Assert.Equal(["worker-1"], Assert.Single(store.Read().Items).Instances.Select(i => i.Id).ToArray());

        // Disconnecting flips Connected immediately.
        store.SetConnected(false);
        Assert.False(store.Read().Connected);
    }

    [Fact]
    public void CommandPolicyEnforcesAllSevenRulesCleanly()
    {
        var store = new AspireHostStore();
        store.SetConnected(true);
        store.Apply(Snapshot("os", "os-a-11111111", commands: [RestartCmd]));
        store.Apply(Snapshot("os", "os-b-22222222", commands: [RestartCmd]));
        store.Apply(Snapshot("os-gateway", "os-gateway", commands: [RestartCmd]));
        store.Apply(Snapshot("intochat", "intochat", commands: [RestartCmd, ResetCmd, DisabledCmd]));
        store.Apply(Snapshot("worker", "worker-1", commands: [RestartCmd]));
        store.Apply(Snapshot("worker", "worker-2", commands: [RestartCmd]));

        var topology = store.Snapshot();

        // 1. Disconnected -> unavailable
        var unavailable = Assert.IsType<RefuseCommand>(CommandPolicy.Evaluate(RuntimeTopology.Disconnected, true, "intochat", "resource-restart", "os-a", ["reset-state"]));
        Assert.Equal(RefusalReasons.Unavailable, unavailable.Reason);

        // 2. Replicated model name -> unknown listing replicas; instance id -> allowed
        var replicated = Assert.IsType<RefuseCommand>(CommandPolicy.Evaluate(topology, true, "worker", "resource-restart", "os-a", ["reset-state"]));
        Assert.Equal(RefusalReasons.Unknown, replicated.Reason);
        Assert.Equal(["worker-1", "worker-2"], replicated.Known!);
        Assert.IsType<AllowCommandNow>(CommandPolicy.Evaluate(topology, true, "worker-1", "resource-restart", "os-a", ["reset-state"]));

        // 3. Unknown or disabled command -> unknown-command listing available ones
        var unknownCmd = Assert.IsType<RefuseCommand>(CommandPolicy.Evaluate(topology, true, "intochat", "bogus", "os-a", ["reset-state"]));
        Assert.Equal(RefusalReasons.UnknownCommand, unknownCmd.Reason);
        Assert.Contains("resource-restart", unknownCmd.Known!);

        var disabled = Assert.IsType<RefuseCommand>(CommandPolicy.Evaluate(topology, true, "intochat", "resource-start", "os-a", ["reset-state"]));
        Assert.Equal(RefusalReasons.UnknownCommand, disabled.Reason);

        // 4. Every replica of the kernel & gateway is self for both person and agent
        foreach (var isPerson in new[] { true, false })
        {
            foreach (var kernelTarget in new[] { "os-a-11111111", "os-b-22222222", "os-gateway" })
            {
                var selfRefusal = Assert.IsType<RefuseCommand>(CommandPolicy.Evaluate(topology, isPerson, kernelTarget, "resource-restart", "os-a", ["reset-state"]));
                Assert.Equal(RefusalReasons.Self, selfRefusal.Reason);
            }
        }

        // 5. Person-only command refused for non-person, allowed for direct person
        var personOnly = Assert.IsType<RefuseCommand>(CommandPolicy.Evaluate(topology, false, "intochat", "reset-state", "os-a", ["reset-state"]));
        Assert.Equal(RefusalReasons.PersonOnly, personOnly.Reason);
        Assert.IsType<AllowCommandNow>(CommandPolicy.Evaluate(topology, true, "intochat", "reset-state", "os-a", ["reset-state"]));

        // 6 & 7. Non-person requesting allowed command gets RequireCommandApproval with Aspire's confirmation message when present
        var approval = Assert.IsType<RequireCommandApproval>(CommandPolicy.Evaluate(topology, false, "intochat", "reset-state", "os-a", []));
        Assert.Equal("Reset Local State on intochat?", approval.CardTitle);
        Assert.Equal("This deletes all local state.", approval.CardBody);
    }

    [Fact]
    public async Task FeedRefusesNonAspireCallersAndPublishesSignalsToActiveBrains()
    {
        var store = new AspireHostStore();
        store.SetConnected(true);
        store.Apply(Snapshot("web-searxng", "web-searxng"));

        var brain = new BrainId("facts");
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services).Register<AspireNeuron>();

        var aspireId = new NeuronId(brain, "aspire", IAspire.KeyPattern);
        var aspire = os.Get<IAspire>(aspireId);
        var feed = os.Get<IAspireFeed>(aspireId);

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("p", "p", brain)))
        {
            Assert.True((await aspire.Resources()).Connected);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => feed.Changed([new AspireResourceChanged("web-searxng", "web-searxng", "Exited", null)]));
        }

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("p", "p"), brain, "conv-1")))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => feed.Changed([new AspireResourceChanged("web-searxng", "web-searxng", "Exited", null)]));
        }

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Module("aspire", brain)))
        {
            await feed.Changed([new AspireResourceChanged("web-searxng", "web-searxng", "Exited", null)]);
        }

        var changed = os.Journal(brain).Signals(aspireId).Select(e => e.Signal).OfType<AspireResourceChanged>().ToArray();
        Assert.Contains(changed, s => s is { Name: "web-searxng", State: "Exited" });
    }

    [Fact]
    public async Task ExecuteAndConsoleLogWorkEndToEndWithConfirmationAndDisconnectRecheck()
    {
        var store = new AspireHostStore();
        store.SetConnected(true);
        store.Apply(Snapshot("web-searxng", "web-searxng", commands: [RestartCmd]));
        store.Apply(Snapshot("worker", "worker-1", commands: [RestartCmd]));
        store.Apply(Snapshot("worker", "worker-2", commands: [RestartCmd]));

        var bridge = new FakeBridge();
        var options = new AspireOptions();
        var brain = new BrainId("facts");
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IAspireRuntimeBridge>(bridge)
            .AddSingleton<IOptions<AspireOptions>>(Options.Create(options))
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services)
            .Register<AspireNeuron>()
            .Register<ConfirmationsNeuron>();

        var aspireId = new NeuronId(brain, "aspire", IAspire.KeyPattern);
        var aspire = os.Get<IAspire>(aspireId);
        var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));

        // ConsoleLog resolves single instance and returns empty for replicated model name.
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("p", "p", brain)))
        {
            Assert.Equal(["line-1", "line-2"], await aspire.ConsoleLog("web-searxng", 10));
            Assert.Empty(await aspire.ConsoleLog("worker", 10));

            // Direct person runs immediately.
            var ok = await aspire.Execute("web-searxng", "resource-restart");
            Assert.True(ok.Accepted);
            Assert.Equal([("web-searxng", "resource-restart")], bridge.Executed);
        }

        // Agent gets pending confirmation; approving while disconnected refuses as unavailable.
        AspireCommandResult pending;
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("p", "p"), brain, "conv-1")))
        {
            pending = await aspire.Execute("web-searxng", "resource-restart");
            Assert.False(pending.Accepted);
            Assert.Equal(RefusalReasons.Pending, pending.Reason);
            Assert.NotNull(pending.Confirmation);
        }

        store.SetConnected(false);
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("p", "p", brain)))
        {
            await inbox.Resolve(pending.Confirmation!, approve: true);
        }
        await os.Idle();

        var refused = os.Journal(brain).Signals(aspireId).Select(e => e.Signal).OfType<AspireCommandRefused>().ToArray();
        Assert.Contains(refused, s => s is { Resource: "web-searxng", Command: "resource-restart", Reason: RefusalReasons.Unavailable });
        Assert.Single(bridge.Executed);
    }

    private static T RoundTrip<T>(T value)
        => (T)RuntimeProtocol.Deserialize(RuntimeProtocol.SerializeToUtf8Bytes(value!, typeof(T)), typeof(T))!;

    private sealed class FakeBridge : IAspireRuntimeBridge
    {
        public List<(string InstanceId, string Command)> Executed { get; } = [];
        public Func<string, string, BridgeCommandReply>? OnExecute { get; set; }

        public Task<BridgeCommandReply> ExecuteAsync(string instanceId, string command, TimeSpan timeout, CancellationToken ct)
        {
            Executed.Add((instanceId, command));
            return Task.FromResult(OnExecute?.Invoke(instanceId, command) ?? new BridgeCommandReply(true));
        }

        public Task<string[]> ConsoleLogAsync(string instanceId, int tail, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult<string[]>(["line-1", "line-2"]);
    }
}
