using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Framework.Tests.Unit.Kernel;

[GenerateSerializer]
public sealed class LedgerState { [Id(0)] public int Value { get; set; } }

[Alias("test.ledger")]
public interface ILedger : INeuron
{
    Task Set(int value);
    Task Clear();
}

[GrainType("test-ledger")]
public sealed class LedgerNeuron([PersistentState("state", "Default")] IPersistentState<LedgerState> state) : Neuron, ILedger
{
    public Task Set(int value) { state.State.Value = value; return state.WriteStateAsync(); }
    public Task Clear() => state.ClearStateAsync();
}

public interface IPlainGrain : IGrainWithStringKey { Task Set(int value); }

[GrainType("test-plain")]
public sealed class PlainGrain([PersistentState("state", "Default")] IPersistentState<LedgerState> state) : Grain, IPlainGrain
{
    public Task Set(int value) { state.State.Value = value; return state.WriteStateAsync(); }
}

public sealed class KernelFacts
{
    [Fact]
    public async Task AFirstWriteRegistersTheNeuronOnceAndTheBrainAnnouncesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        using var created = host.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<InstanceCreated>();
        await As(host, "alice", "a");
        var ledger = host.Get<ILedger>("a/ledger/1");

        await ledger.Set(1);
        await ledger.Set(2);

        var instance = Assert.Single(await Instances(host, "test-ledger", ct));
        Assert.Equal(("test-ledger", "a/ledger/1", "alice"), (instance.Type, instance.Key, instance.Creator));
        // The caller's authorization writes the brain's own access record first; that is an instance too.
        var signal = await created.Reader.ReadAsync(ct);
        while (signal.Type != "test-ledger") { signal = await created.Reader.ReadAsync(ct); }
        Assert.Equal(("test-ledger", "a/ledger/1"), (signal.Type, signal.Key));
        Assert.Equal("brain/a", signal.Publisher.Name);
    }

    [Fact]
    public async Task AGrainThatIsNotANeuronOrHasNoStateNeverRegisters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        await As(host, "alice", "a");

        await host.Grains.GetGrain<IPlainGrain>("a/plain").Set(1);
        await host.Get<ITestEmitter>("a/emitter").Emit(1);

        Assert.DoesNotContain(await Instances(host, null, ct), instance => instance.Type is "test-plain" or "test-emitter");
    }

    [Fact]
    public async Task ClearingStateRetiresTheInstanceAndAnnouncesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        using var retired = host.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<InstanceRetired>();
        await As(host, "alice", "a");
        var ledger = host.Get<ILedger>("a/ledger/1");
        await ledger.Set(1);

        await ledger.Clear();

        Assert.Empty(await Instances(host, "test-ledger", ct));
        Assert.Equal("a/ledger/1", (await retired.Reader.ReadAsync(ct)).Key);
    }

    [Fact]
    public async Task RetireDropsTheEntryOnlyInTheCallersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        await As(host, "alice", "a");
        await host.Get<ILedger>("a/ledger/1").Set(1);
        var kernel = new Framework.Kernel(host.Grains);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => kernel.Retire("test-ledger", "b/ledger/1", ct));
        await kernel.Retire("test-ledger", "a/ledger/1", ct);

        Assert.Empty(await Instances(host, "test-ledger", ct));
    }

    [Fact]
    public async Task ABrainCannotListAnotherBrainsInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        await As(host, "bob", "b");
        await host.Get<ILedger>("b/ledger/1").Set(1);
        await As(host, "alice", "a");
        await host.Get<ILedger>("a/ledger/1").Set(1);

        Assert.All(await Instances(host, null, ct), instance => Assert.True(BrainKey.BelongsTo(instance.Key, "a"), instance.Key));
        Assert.Contains(await Instances(host, null, ct), instance => instance.Key == "a/ledger/1");
        var running = await new Framework.Kernel(host.Grains).Running(ct);
        Assert.Contains(running, instance => instance.Key == "a/ledger/1");
        Assert.DoesNotContain(running, instance => instance.Key == "b/ledger/1");
    }

    [Fact]
    public async Task InstancesOfAContractResolveItsGrainType()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        await As(host, "alice", "a");
        await host.Get<ILedger>("a/ledger/1").Set(1);

        List<Instance> listed = [];
        await foreach (var instance in new Framework.Kernel(host.Grains).Instances<ILedger>(ct)) { listed.Add(instance); }

        Assert.Equal("a/ledger/1", Assert.Single(listed).Key);
    }

    [Fact]
    public void NothingPublicCanRegisterAnInstance()
    {
        Type[] surface = [typeof(IKernelView), typeof(IKernel), typeof(Framework.Kernel)];
        Assert.DoesNotContain(surface.SelectMany(type => type.GetMethods()), method => method.Name.Contains("Register", StringComparison.Ordinal));
        Assert.False(typeof(IInstanceDirectory).IsPublic);
        Assert.False(typeof(INeuron).IsAssignableFrom(typeof(IInstanceDirectory)));
        Assert.False(typeof(INeuron).IsAssignableFrom(typeof(IInstanceTypes)));
    }

    // Not async: the stamp is an AsyncLocal and must stay with the calling fact.
    private static Task As(ModuleBrain host, string principal, string brain)
    {
        CallerContextStamper.Stamp(new()
        { PrincipalId = principal, AccountId = principal, BrainId = brain, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        return host.AuthorizeCallerAsync();
    }

    private static async Task<List<Instance>> Instances(ModuleBrain host, string? type, CancellationToken ct)
    {
        List<Instance> listed = [];
        await foreach (var instance in new Framework.Kernel(host.Grains).Instances(type, ct)) { listed.Add(instance); }
        return listed;
    }
}
