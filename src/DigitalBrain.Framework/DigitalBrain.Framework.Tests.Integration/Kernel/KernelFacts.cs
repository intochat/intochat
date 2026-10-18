using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Framework.Tests.Integration.Kernel;

[GenerateSerializer]
public sealed class LedgerState { [Id(0)] public int Value { get; set; } }

[Alias("test.ledger")]
public interface ILedger : INeuron
{
    Task Set(int value);
    Task Clear();
}

[GrainType("test-ledger")]
public sealed class LedgerNeuron([PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<LedgerState> state) : Neuron, ILedger
{
    public Task Set(int value) { state.State.Value = value; return state.WriteStateAsync(); }
    public Task Clear() => state.ClearStateAsync();
}

public interface IPlainGrain : IGrainWithStringKey { Task Set(int value); }

[GrainType("test-plain")]
public sealed class PlainGrain([PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<LedgerState> state) : Grain, IPlainGrain
{
    public Task Set(int value) { state.State.Value = value; return state.WriteStateAsync(); }
}

public sealed class KernelFacts(FrameworkHost host)
{
    [Fact]
    public async Task UnscopedTypeDiscoveryReadsCompiledTypesWithoutOpeningABrainJournal()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        Assert.False(CallerContextStamper.TryGet(out _));
        var types = await brain.Services.GetRequiredService<IKernelView>().Types(ct);
        Assert.NotEmpty(types);
        Assert.False(typeof(ICompiledTypeTable).IsPublic);
        Assert.False(typeof(INeuron).IsAssignableFrom(typeof(ICompiledTypeTable)));
    }

    [Fact]
    public async Task AFirstWriteRegistersTheNeuronOnceAndTheBrainAnnouncesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        await using var created = await brain.Observe<InstanceCreated>(brain.Get<IBrain>(alice.BrainId), ct);
        var ledger = brain.Get<ILedger>(alice.Key("ledger/1"));

        await ledger.Set(1);
        await ledger.Set(2);

        var instance = Assert.Single(await Instances(brain, "test-ledger", ct));
        Assert.Equal(("test-ledger", alice.Key("ledger/1"), alice.PrincipalId), (instance.Type, instance.Key, instance.Creator));
        var signal = await created.NextAsync(signal => signal.Type == "test-ledger", ct);
        Assert.Equal(alice.Key("ledger/1"), signal.Key);
        Assert.Equal("brain/" + alice.BrainId, signal.Publisher.Name);
    }

    [Fact]
    public async Task AGrainThatIsNotANeuronOrHasNoStateNeverRegisters()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();

        await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IPlainGrain>(alice.Key("plain")).Set(1);
        await brain.Get<ITestEmitter>(alice.Key("emitter")).Emit(1);

        Assert.DoesNotContain(await Instances(brain, null, ct), instance => instance.Type is "test-plain" or "test-emitter");
    }

    [Fact]
    public async Task ABareBrainKeyIsTheBrainItselfAndNeverRegisters()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();

        await brain.Get<ILedger>(alice.BrainId).Set(1);

        Assert.Empty(await Instances(brain, "test-ledger", ct));
    }

    [Fact]
    public async Task ClearingStateRetiresTheInstanceAndAnnouncesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        await using var retired = await brain.Observe<InstanceRetired>(brain.Get<IBrain>(alice.BrainId), ct);
        var ledger = brain.Get<ILedger>(alice.Key("ledger/1"));
        await ledger.Set(1);

        await ledger.Clear();

        Assert.Empty(await Instances(brain, "test-ledger", ct));
        Assert.Equal(alice.Key("ledger/1"), (await retired.NextAsync(ct: ct)).Key);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheDirectoryCompactsTheOldestInactiveEntriesWhenATypeExceedsItsLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .ConfigureServices(services => services.Configure<InstanceDirectoryOptions>(options => options.Limit = 4))
            .StartAsync(ct);
        var alice = await brain.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var first = brain.Get<ILedger>(alice.Key("ledger/1"));
        await first.Set(1);
        await brain.DeactivateAsync(first, ct);
        var second = brain.Get<ILedger>(alice.Key("ledger/2"));
        await second.Set(2);
        await brain.DeactivateAsync(second, ct);

        for (var i = 3; i <= 6; i++) { await brain.Get<ILedger>(alice.Key($"ledger/{i}")).Set(i); }

        var listed = await Instances(brain, "test-ledger", ct);
        Assert.Equal(4, listed.Count);
        Assert.DoesNotContain(listed, instance => instance.Key is var key && (key == alice.Key("ledger/1") || key == alice.Key("ledger/2")));
        Assert.Contains(listed, instance => instance.Key == alice.Key("ledger/6"));
    }

    [Fact]
    public async Task RetireDropsTheEntryOnlyInTheCallersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        var bob = await host.PersonAsync("bob", ct);
        using var caller = alice.AsCaller();
        await brain.Get<ILedger>(alice.Key("ledger/1")).Set(1);
        var kernel = new Framework.Kernel(brain.Services.GetRequiredService<IGrainFactory>());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => kernel.Retire("test-ledger", bob.Key("ledger/1"), ct));
        await kernel.Retire("test-ledger", alice.Key("ledger/1"), ct);

        Assert.Empty(await Instances(brain, "test-ledger", ct));
    }

    [Fact]
    public async Task ABrainCannotListAnotherBrainsInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        var bob = await host.PersonAsync("bob", ct);
        using (bob.AsCaller()) { await brain.Get<ILedger>(bob.Key("ledger/1")).Set(1); }
        using var caller = alice.AsCaller();
        await brain.Get<ILedger>(alice.Key("ledger/1")).Set(1);

        Assert.All(await Instances(brain, null, ct), instance => Assert.True(BrainKey.BelongsTo(instance.Key, alice.BrainId), instance.Key));
        Assert.Contains(await Instances(brain, null, ct), instance => instance.Key == alice.Key("ledger/1"));
        var running = await new Framework.Kernel(brain.Services.GetRequiredService<IGrainFactory>()).Running(ct);
        Assert.Contains(running, instance => instance.Key == alice.Key("ledger/1"));
        Assert.DoesNotContain(running, instance => instance.Key == bob.Key("ledger/1"));
    }

    [Fact]
    public async Task InstancesOfAContractResolveItsGrainType()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        await brain.Get<ILedger>(alice.Key("ledger/1")).Set(1);

        List<Instance> listed = [];
        await foreach (var instance in new Framework.Kernel(brain.Services.GetRequiredService<IGrainFactory>()).Instances<ILedger>(ct)) { listed.Add(instance); }

        Assert.Equal(alice.Key("ledger/1"), Assert.Single(listed).Key);
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

    private static async Task<List<Instance>> Instances(ITestBrain brain, string? type, CancellationToken ct)
    {
        List<Instance> listed = [];
        await foreach (var instance in new Framework.Kernel(brain.Services.GetRequiredService<IGrainFactory>()).Instances(type, ct)) { listed.Add(instance); }
        return listed;
    }
}
