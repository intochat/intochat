using System.Text.Json;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Units;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Components;

[DefaultNeuronType("facts.slot.store")]
public interface ISlotTargetStore
{
    Task<string> WhoAmI();
}

[Neuron("facts.slot.store")]
public sealed class SlotTargetStoreNeuron : Neuron, ISlotTargetStore
{
    public Task<string> WhoAmI() => Task.FromResult(Id.ToString());
}

[NeuronAlias("facts.slot.search")]
public interface IDefaultKeySearch
{
    const string DefaultKey = "main";
    Task<string> WhoAmI();
}

[Neuron("facts.slot.search")]
public sealed class DefaultKeySearchNeuron : Neuron, IDefaultKeySearch
{
    public Task<string> WhoAmI() => Task.FromResult(Id.ToString());
}

[DefaultNeuronType("facts.slot.consumer")]
public interface ISlotConsumer
{
    Task<string> ReadCustomers();
}

[Neuron("facts.slot.consumer")]
public sealed partial class SlotConsumerNeuron : Neuron, ISlotConsumer
{
    [Slot]
    private partial ISlotTargetStore Customers { get; }

    public Task<string> ReadCustomers() => Customers.WhoAmI();
}

[DefaultNeuronType("facts.slot.default-consumer")]
public interface IDefaultSlotConsumer
{
    Task<string> ReadSearch();
    Task<string> ReadProfiles();
}

[Neuron("facts.slot.default-consumer")]
public sealed partial class DefaultSlotConsumerNeuron : Neuron, IDefaultSlotConsumer
{
    [Slot]
    private partial IDefaultKeySearch Search { get; }

    [Slot(Key = "customer_profiles")]
    private partial ISlotTargetStore Profiles { get; }

    public Task<string> ReadSearch() => Search.WhoAmI();
    public Task<string> ReadProfiles() => Profiles.WhoAmI();
}

[Module("facts-slots")]
public sealed class SlotFactsModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class SlotFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ABoundSlotReachesItsTarget(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(kind, [typeof(SlotFactsModule)], ct: ct);

        var ada = os.Person("ada");
        var targetId = new NeuronId(ada.Brain, "facts.slot.store", "cust-42");
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("facts.slot.consumer"),
            [new Binding("customers", new BindingTarget.Local(targetId))],
            ct);

        var consumer = ada.Get<ISlotConsumer>(new NeuronId(ada.Brain, "facts.slot.consumer", "c1"));
        var reached = await consumer.ReadCustomers();

        Assert.Equal(targetId.ToString(), reached);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ASlotWithADefaultKeyBindsToMineWithNoInstallStep(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(kind, [typeof(SlotFactsModule)], ct: ct);

        var ada = os.Person("ada");
        var consumer = ada.Get<IDefaultSlotConsumer>(new NeuronId(ada.Brain, "facts.slot.default-consumer", "d1"));

        var search = await consumer.ReadSearch();
        var profiles = await consumer.ReadProfiles();

        Assert.Equal(new NeuronId(ada.Brain, "facts.slot.search", "main").ToString(), search);
        Assert.Equal(new NeuronId(ada.Brain, "facts.slot.store", "customer_profiles").ToString(), profiles);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ARequiredSlotWithNoBindingRefusesActivation(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(kind, [typeof(SlotFactsModule)], ct: ct);

        var ada = os.Person("ada");
        var consumer = ada.Get<ISlotConsumer>(new NeuronId(ada.Brain, "facts.slot.consumer", "unbound-1"));

        var error = await Assert.ThrowsAsync<ActivationRefusedException>(() => consumer.ReadCustomers());
        Assert.Equal(ActivationRefusedException.SlotUnbound, error.Reason);
        Assert.Contains("customers", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AnInstallRecordWrittenBeforeTypedBindingsStillLoads(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;

        // Pre-Typed JSON payload (no "Typed" property at all).
        const string legacyJson = """
            {
              "Records": [
                {
                  "Alias": "legacy.crm",
                  "NeedsHash": "hash-v1",
                  "Bindings": {
                    "db": "prod-postgres",
                    "mailer": "sendgrid"
                  },
                  "Schedules": [],
                  "ConsentedAt": "2026-01-15T12:00:00Z"
                }
              ],
              "Pending": []
            }
            """;

        var state = JsonSerializer.Deserialize<InstallsState>(legacyJson);
        Assert.NotNull(state);
        var record = Assert.Single(state.Records);
        Assert.Null(record.Typed);

        var effective = record.EffectiveBindings();
        Assert.Contains(effective, b => b.Slot == "db" && b.Target is BindingTarget.Connection { ConnectionKey: "prod-postgres" });
        Assert.Contains(effective, b => b.Slot == "mailer" && b.Target is BindingTarget.Connection { ConnectionKey: "sendgrid" });

        // Also verify round-trip through live IInstalls on both runtimes.
        await using var os = await JourneyOS.Start(kind, [typeof(SlotFactsModule)], ct: ct);
        var ada = os.Person("ada");
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        var targetId = new NeuronId(ada.Brain, "facts.slot.store", "legacy-check");
        await installs.BindComponent(
            new ComponentId("legacy.crm"),
            [new Binding("db", new BindingTarget.Connection("prod-postgres")), new Binding("customers", new BindingTarget.Local(targetId))],
            ct);

        var loaded = await installs.Describe("legacy.crm", ct);
        Assert.NotNull(loaded);
        var loadedEffective = loaded.EffectiveBindings();
        Assert.Contains(loadedEffective, b => b.Slot == "db" && b.Target is BindingTarget.Connection { ConnectionKey: "prod-postgres" });
        Assert.Contains(loadedEffective, b => b.Slot == "customers" && b.Target is BindingTarget.Local { Neuron: var n } && n == targetId);
    }
}
