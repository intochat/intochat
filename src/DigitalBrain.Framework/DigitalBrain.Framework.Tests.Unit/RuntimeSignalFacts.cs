using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class RuntimeSignalFacts
{
    [Fact]
    public async Task SiloSubscriptionsReceiveOrdinaryNeuronSignalsAndFilterByType()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var signals = brain.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Number>();
        await brain.Get<ITestEmitter>("one").EmitText("ignored");
        await brain.Get<ITestEmitter>("one").Emit(1);
        await brain.Get<ITestEmitter>("two").Emit(2);
        Assert.Equal(1, (await signals.Reader.ReadAsync(ct)).Value);
        Assert.Equal(2, (await signals.Reader.ReadAsync(ct)).Value);
        Assert.False(signals.Reader.TryRead(out _));
        signals.Dispose();
        await brain.Get<ITestEmitter>("one").Emit(3);
        await signals.Reader.Completion;
    }

    [Fact]
    public async Task StartupAnnouncesModulesThroughTheSharedHubAndInventoryRemainsAvailable()
    {
        var hub = new LocalSignalHub(NullLogger<LocalSignalHub>.Instance);
        var inventory = new ModuleInventory([typeof(RuntimeFixtureModule)]);
        using var subscription = hub.Subscribe<ModuleLoaded>();
        await new RuntimeStartupTask(inventory, hub).Execute(TestContext.Current.CancellationToken);
        Assert.Equal(typeof(RuntimeFixtureModule).AssemblyQualifiedName,
            (await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken)).ModuleType);
        Assert.Equal([typeof(RuntimeFixtureModule)], inventory.Types);
    }
    public sealed class RuntimeFixtureModule() : Module(new(nameof(RuntimeFixtureModule)))
    {
        public override void Configure(ISiloBuilder silo) { }
    }
}
