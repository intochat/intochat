using DigitalBrain.Capabilities;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Synapses;
using DigitalBrain.Units;
using Microsoft.Extensions.DependencyInjection;
using SlotUnboundException = DigitalBrain.Units.SlotUnboundException;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit.Synapses;

public sealed class BrokerSlotResolverFacts
{
    private interface INotesPort
    {
        NeuronId BoundTo { get; }
    }

    private sealed class FakeNotesProxy(NeuronId boundTo) : INotesPort
    {
        public NeuronId BoundTo { get; } = boundTo;
    }

    [Fact]
    public async Task ResolvesLocalBindingViaNeuronProxies()
    {
        var brain = new BrainId("ada");
        var state = new NeuronId(brain, "unit.researcher", "main");
        var target = new NeuronId(brain, "postgres.table", "research/notes");
        var record = new InstallRecord(
            state.Type,
            "hash-1",
            new Dictionary<string, string>(),
            [],
            DateTimeOffset.UtcNow,
            Typed: [new Binding("notes", new BindingTarget.Local(target))]);

        using var services = BuildServices(record);
        var resolver = new BrokerSlotResolver(services);
        var slot = new SlotDescriptor("notes", typeof(INotesPort), new CapabilityId("notes.port", 1), 0);

        var resolved = await resolver.Resolve(state, slot, TestContext.Current.CancellationToken);

        var proxy = Assert.IsAssignableFrom<INotesPort>(resolved);
        Assert.Equal(target, proxy.BoundTo);
    }

    [Fact]
    public async Task ResolvesOfferedBindingViaNeuronProxies()
    {
        var ada = new BrainId("ada");
        var bob = new BrainId("bob");
        var state = new NeuronId(bob, "unit.researcher", "main");
        var offeredTarget = new NeuronId(ada, "postgres.table", "research/shared");
        var record = new InstallRecord(
            state.Type,
            "hash-2",
            new Dictionary<string, string>(),
            [],
            DateTimeOffset.UtcNow,
            Typed: [new Binding("shared", new BindingTarget.Offered(ada, offeredTarget))]);

        using var services = BuildServices(record);
        var resolver = new BrokerSlotResolver(services);
        var slot = new SlotDescriptor("shared", typeof(INotesPort), new CapabilityId("notes.port", 1), 0);

        var resolved = await resolver.Resolve(state, slot, TestContext.Current.CancellationToken);

        var proxy = Assert.IsAssignableFrom<INotesPort>(resolved);
        Assert.Equal(offeredTarget, proxy.BoundTo);
    }

    [Fact]
    public async Task ThrowsSlotUnboundWhenSlotNotPresent()
    {
        var brain = new BrainId("ada");
        var state = new NeuronId(brain, "unit.researcher", "main");
        var record = new InstallRecord(
            state.Type,
            "hash-3",
            new Dictionary<string, string>(),
            [],
            DateTimeOffset.UtcNow,
            Typed: []);

        using var services = BuildServices(record);
        var resolver = new BrokerSlotResolver(services);
        var slot = new SlotDescriptor("missing", typeof(INotesPort), new CapabilityId("notes.port", 1), 0);

        await Assert.ThrowsAsync<SlotUnboundException>(
            () => resolver.Resolve(state, slot, TestContext.Current.CancellationToken));
    }

    private static ServiceProvider BuildServices(InstallRecord record)
    {
        var services = new ServiceCollection();
        services.AddSingleton<INeuronProxies>(new FakeProxies(new FakeInstalls(record)));
        return services.BuildServiceProvider();
    }

    private sealed class FakeProxies(IInstalls installs) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IInstalls))
            {
                return (T)(object)installs;
            }
            if (typeof(T) == typeof(INotesPort))
            {
                return (T)(object)new FakeNotesProxy(id);
            }
            throw new InvalidOperationException($"Unexpected proxy request for {typeof(T).Name}.");
        }
    }

    private sealed class FakeInstalls(InstallRecord record) : IInstalls
    {
        public Task<InstallRecord?> Describe(string alias, CancellationToken ct)
            => Task.FromResult<InstallRecord?>(string.Equals(alias, record.Alias, StringComparison.Ordinal) ? record : null);

        public Task<InstallOutcome> Install(InstallRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> Validate(InstallRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task Retire(string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task Uninstall(string alias, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelPending(string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstallRecord>> List(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PendingInstall>> Pending(CancellationToken ct) => throw new NotSupportedException();
        public Task Rewire(string alias, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BindComponent(ComponentId component, Binding[] bindings, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
