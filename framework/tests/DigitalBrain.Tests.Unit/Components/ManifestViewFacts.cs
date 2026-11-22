using System.ComponentModel;
using DigitalBrain.Capabilities;
using DigitalBrain.Components;
using DigitalBrain.Modules;
using DigitalBrain.Units;

namespace DigitalBrain.Tests.Unit.Components;

public sealed class ManifestViewFacts
{
    [Fact]
    public void ModuleManifest_From_projects_id_provides_and_capability_slot_requires()
    {
        var component = new ComponentManifest(
            Id: new ComponentId("module.orders"),
            Slots:
            [
                new DigitalBrain.Components.SlotNeed("storage", typeof(ITestStorageCapability)),
                new DigitalBrain.Components.SlotNeed("helper", typeof(IPlainHelperContract)),
                new DigitalBrain.Components.SlotNeed("storageDup", typeof(ITestStorageCapability)),
            ],
            Kind: ComponentKind.Module,
            Provides: [typeof(ITestOrdersCapability), typeof(IPlainHelperContract)]);

        var manifest = ModuleManifest.From(component);

        Assert.Equal("orders", manifest.Id);
        Assert.Equal([new CapabilityId("orders", 1)], manifest.Provides);
        Assert.Equal([new CapabilityId("test-storage", 1)], manifest.Requires);
        Assert.Empty(manifest.Needs);
        Assert.Empty(manifest.Settings);
        Assert.Empty(manifest.Secrets);
    }

    [Fact]
    public void ModuleManifest_From_keeps_id_without_module_prefix()
    {
        var component = new ComponentManifest(
            Id: new ComponentId("custom.id"),
            Slots: [],
            Kind: ComponentKind.Module);

        var manifest = ModuleManifest.From(component);

        Assert.Equal("custom.id", manifest.Id);
    }

    [Fact]
    public void ModuleManifest_Of_merges_capability_slots_from_registered_ComponentManifest()
    {
        var componentId = new ComponentId("module.test.slot-merged");
        ComponentRegistry.Register(typeof(SlotMergedModule), componentId);
        ComponentManifests.Register(
            typeof(SlotMergedModule),
            new ComponentManifest(
                Id: componentId,
                Slots:
                [
                    new DigitalBrain.Components.SlotNeed("storage", typeof(ITestStorageCapability)),
                    new DigitalBrain.Components.SlotNeed("plain", typeof(IPlainHelperContract)),
                ],
                Kind: ComponentKind.Module));

        var manifest = ModuleManifest.Of(typeof(SlotMergedModule));

        Assert.Equal("test.slot-merged", manifest.Id);
        Assert.Contains(new CapabilityId("test-orders", 1), manifest.Requires);
        Assert.Contains(new CapabilityId("test-storage", 1), manifest.Requires);
        Assert.Equal(2, manifest.Requires.Count);
    }

    [Fact]
    public void SynapseNeeds_From_projects_alias_capability_slots_reactions_and_view()
    {
        var component = new ComponentManifest(
            Id: new ComponentId("unit.digest"),
            Slots:
            [
                new DigitalBrain.Components.SlotNeed("db", typeof(ITestSqlCapability)),
                new DigitalBrain.Components.SlotNeed("helper", typeof(IPlainHelperContract)),
            ],
            Face: new ComponentFace("Digest", "Daily summary"),
            Kind: ComponentKind.Unit,
            Reacts: [typeof(TestOrderPlacedSignal), typeof(UnmarkedSignal)]);

        var needs = SynapseNeeds.From(component);

        Assert.Equal("digest", needs.Alias);
        var slot = Assert.Single(needs.Slots);
        Assert.Equal("db", slot.Name);
        Assert.Equal(new CapabilityId("sql-read", 1), slot.Capability);
        Assert.Equal("read-only SQL", slot.ContractDisplayName);
        Assert.Equal(["orders.placed", nameof(UnmarkedSignal)], needs.Reactions);
        Assert.Empty(needs.Schedules);
        Assert.True(needs.HasView);
    }

    [Fact]
    public void SynapseNeeds_From_sets_HasView_false_when_face_is_null()
    {
        var component = new ComponentManifest(
            Id: new ComponentId("plain-unit"),
            Slots: [],
            Kind: ComponentKind.Unit);

        var needs = SynapseNeeds.From(component);

        Assert.Equal("plain-unit", needs.Alias);
        Assert.False(needs.HasView);
        Assert.Empty(needs.Slots);
        Assert.Empty(needs.Reactions);
    }

    [Capability("test-storage", 1)]
    private interface ITestStorageCapability;

    [Capability("orders", 1)]
    private interface ITestOrdersCapability;

    [Capability("sql-read", 1)]
    [Description("read-only SQL")]
    private interface ITestSqlCapability;

    private interface IPlainHelperContract;

    [Signal("orders.placed")]
    private sealed record TestOrderPlacedSignal;

    private sealed record UnmarkedSignal;

    [Module("test.slot-merged")]
    private sealed class SlotMergedModule : Module
    {
        public static ModuleManifest Manifest { get; } = new(
            Provides: [],
            Requires: [new CapabilityId("test-orders", 1)],
            Needs: [],
            Settings: [],
            Secrets: []);
    }
}
