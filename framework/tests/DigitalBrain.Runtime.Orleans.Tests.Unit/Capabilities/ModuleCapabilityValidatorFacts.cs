using DigitalBrain.Modules;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Secrets;
using DigitalBrain.Testing.Conformance.Storage;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit.Capabilities;

public sealed class ModuleCapabilityValidatorFacts
{
    private static readonly CapabilityRegistry Registry = new();

    [Fact]
    public void Unknown_major_is_refused_naming_module_and_capability()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModuleCapabilityValidator.Validate(Registry, [typeof(StorageTwoModule)]));

        Assert.Contains("Module 'test.storage2' provides unknown capability storage@2.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provides_without_an_assignable_neuron_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ModuleCapabilityValidator.Validate(Registry, [new ModuleCandidate("x", [Capability.Storage.V1], [typeof(InMemorySecrets)])]));

        Assert.Contains("Module 'x' provides storage@1 but contains no neuron implementing IStorage.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_modules_providing_a_single_connection_capability_are_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModuleCapabilityValidator.Validate(Registry,
        [
            new ModuleCandidate("a", [Capability.Secrets.V1], [typeof(InMemorySecrets)]),
            new ModuleCandidate("b", [Capability.Secrets.V1], [typeof(InMemorySecrets)]),
        ]));

        Assert.Contains("'a'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'b'", error.Message, StringComparison.Ordinal);
        Assert.Contains("secrets@1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_modules_with_an_in_memory_storage_neuron_pass_and_many_are_fine()
    {
        ModuleCapabilityValidator.Validate(Registry,
        [
            new ModuleCandidate("a", [Capability.Storage.V1], [typeof(InMemoryStorage)]),
            new ModuleCandidate("b", [Capability.Storage.V1], [typeof(InMemoryStorage)]),
        ]);
    }

    [Fact]
    public void Accepted_binding_satisfies_the_neuron_check()
    {
        var registry = new CapabilityRegistry();
        registry.AcceptBinding(Capability.Storage.V1, typeof(ILegacy));

        ModuleCapabilityValidator.Validate(registry, [new ModuleCandidate("legacy", [Capability.Storage.V1], [typeof(LegacyNeuron)])]);
    }

    [Fact]
    public void Module_types_without_provides_pass()
    {
        ModuleCapabilityValidator.Validate(Registry, [typeof(PlainModule)]);
    }

    [Fact]
    public void Module_requiring_unregistered_capability_from_component_slot_is_refused()
    {
        var componentId = new DigitalBrain.Components.ComponentId("module.test.slot-requires-missing");
        DigitalBrain.Components.ComponentRegistry.Register(typeof(SlotRequiresMissingModule), componentId);
        DigitalBrain.Components.ComponentManifests.Register(
            typeof(SlotRequiresMissingModule),
            new DigitalBrain.Components.ComponentManifest(
                Id: componentId,
                Slots: [new DigitalBrain.Components.SlotNeed("missing", typeof(IMissingCapability))],
                Kind: DigitalBrain.Components.ComponentKind.Module));

        var error = Assert.Throws<InvalidOperationException>(
            () => ModuleCapabilityValidator.Validate(Registry, [typeof(SlotRequiresMissingModule)]));

        Assert.Contains("test.slot-requires-missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("missing-cap@1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_requiring_registered_capability_from_component_slot_passes()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new CapabilityId("provided-cap", 1), typeof(IProvidedCapability), CapabilityConnectionMode.ManyConnections);

        var componentId = new DigitalBrain.Components.ComponentId("module.test.slot-requires-present");
        DigitalBrain.Components.ComponentRegistry.Register(typeof(SlotRequiresPresentModule), componentId);
        DigitalBrain.Components.ComponentManifests.Register(
            typeof(SlotRequiresPresentModule),
            new DigitalBrain.Components.ComponentManifest(
                Id: componentId,
                Slots: [new DigitalBrain.Components.SlotNeed("provided", typeof(IProvidedCapability))],
                Kind: DigitalBrain.Components.ComponentKind.Module));

        ModuleCapabilityValidator.Validate(registry, [typeof(SlotRequiresPresentModule)]);
    }

    public interface ILegacy;

    [Capability("missing-cap", 1)]
    public interface IMissingCapability;

    [Capability("provided-cap", 1)]
    public interface IProvidedCapability;

    private sealed class LegacyNeuron : ILegacy;
}

[Module("test.storage2")]
public sealed class StorageTwoModule : Module
{
    public static ModuleManifest Manifest { get; } = new([new CapabilityId("storage", 2)], [], [], [], []);
}

[Module("test.plain")]
public sealed class PlainModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

[Module("test.slot-requires-missing")]
public sealed class SlotRequiresMissingModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

[Module("test.slot-requires-present")]
public sealed class SlotRequiresPresentModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}
