using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Components;

public sealed class ManifestViewFacts
{
    [Fact]
    public void AppManifest_From_projects_face_operations_and_slots()
    {
        var component = new ComponentManifest(
            Id: new ComponentId("app.notes"),
            Slots:
            [
                new SlotNeed("files", typeof(ITestStorageCap)),
                new SlotNeed("notes", typeof(ITestNotesContract)),
            ],
            Face: new ComponentFace("Notes", "Personal notes", Icon: "note", Category: "Workspace"),
            Operations:
            [
                new OperationSpec("create", "Creates a note"),
                new OperationSpec("delete", "Deletes a note"),
            ],
            Kind: ComponentKind.App);

        var manifest = AppManifest.From(component);

        Assert.Equal("Notes", manifest.Title);
        Assert.Equal("Personal notes", manifest.Description);
        Assert.Equal("note", manifest.Icon);
        Assert.Equal("Workspace", manifest.Category);
        Assert.Equal(
            [new AppOperation("create", "Creates a note"), new AppOperation("delete", "Deletes a note")],
            manifest.Operations);
        Assert.Equal(2, manifest.Slots.Length);
        Assert.Equal("files", manifest.Slots[0].Name);
        Assert.Equal("storage@1", manifest.Slots[0].Capability);
        Assert.Equal("", manifest.Slots[0].Connector);
        Assert.Equal("notes", manifest.Slots[1].Name);
        Assert.Equal("notes.store", manifest.Slots[1].Connector);
        Assert.Null(manifest.Slots[1].Capability);

        var validated = manifest.Validated();
        Assert.NotNull(validated);
    }

    [Fact]
    public void AppManifest_From_falls_back_to_id_without_app_prefix_when_face_is_missing()
    {
        var component = new ComponentManifest(
            Id: new ComponentId("app.scratch"),
            Slots: [],
            Kind: ComponentKind.App);

        var manifest = AppManifest.From(component);

        Assert.Equal("scratch", manifest.Title);
        Assert.Equal(string.Empty, manifest.Description);
        Assert.Empty(manifest.Operations);
        Assert.Empty(manifest.Slots);
    }

    [Fact]
    public void AppPackageNeeds_From_projects_slots_signals_and_commands()
    {
        var component = new ComponentManifest(
            Id: new ComponentId("app.inbox"),
            Slots:
            [
                new SlotNeed("files", typeof(ITestStorageCap), Required: true),
                new SlotNeed("notes", typeof(ITestNotesContract), Required: false),
            ],
            Operations:
            [
                new OperationSpec("refresh", "Refreshes the inbox"),
            ],
            Kind: ComponentKind.App,
            Emits: [typeof(TestNoteCreatedSignal)],
            Reacts: [typeof(TestMailReceivedSignal), typeof(TestNoteCreatedSignal), typeof(PlainSignal)]);

        var needs = AppPackageNeeds.From(component);

        Assert.Equal(2, needs.Slots.Length);
        Assert.Equal("files", needs.Slots[0].Name);
        Assert.Equal(typeof(ITestStorageCap).FullName, needs.Slots[0].Contract);
        Assert.True(needs.Slots[0].Required);
        Assert.Equal("notes", needs.Slots[1].Name);
        Assert.Equal(typeof(ITestNotesContract).FullName, needs.Slots[1].Contract);
        Assert.False(needs.Slots[1].Required);

        Assert.Equal(["notes.created", "mail.received", nameof(PlainSignal)], needs.Signals);
        Assert.Empty(needs.Schedules);
        var command = Assert.Single(needs.Commands);
        Assert.Equal("refresh", command.Name);
        Assert.Equal("Refreshes the inbox", command.Description);
    }

    [Capability("storage", 1)]
    private interface ITestStorageCap;

    [DefaultNeuronType("notes.store")]
    private interface ITestNotesContract;

    [Signal("notes.created")]
    private sealed record TestNoteCreatedSignal;

    [Signal("mail.received")]
    private sealed record TestMailReceivedSignal;

    private sealed record PlainSignal;
}
