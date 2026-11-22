using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using HostAppRef = IntoChat.OS.Host.Primitives.AppRef;

namespace IntoChat.OS.Tests.Integration.Modules.Catalog.Apps;

public sealed class AppInstallationBindingFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task LegacyAppStateWithConnectionsMigratesToInstallRecordTypedBindings(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, f => f.Postgres(), ct);
        var ada = os.Person("ada");
        var appRef = HostAppRef.Of(ada.Brain, "bookmarks");

        var legacyState = new AppState
        {
            Manifest = new AppManifest(
                "Bookmarks",
                "Saved links",
                [],
                [],
                [new AppSlot("tables", "postgres")]),
            Spec = "Bookmarks app",
            Version = "0.1.0",
            Status = AppStatus.Live,
            BrainScopeId = ada.Brain.Value,
            ConnectionEntries = [new StringEntry("tables", "c1")],
        };
        var legacyJson = JsonSerializer.Serialize(legacyState, RuntimeProtocol.OptionsFor(typeof(AppState)));
        await os.Runtime.SeedState(appRef.Neuron, legacyJson, ct);

        // Reading the app activates AppNeuron, triggering legacy connection migration into IInstalls.
        var snapshot = await ada.Get<IApp>(appRef.Neuron).Read();
        Assert.Equal("c1", snapshot.Connections["tables"]);

        var expectedKey = $"{ada.Brain.Value}/connection/postgres/c1";
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        var record = await installs.Describe("app.bookmarks", ct);
        Assert.NotNull(record);
        Assert.NotNull(record.Typed);
        Assert.Contains(
            record.Typed,
            b => b.Slot == "tables" && b.Target is BindingTarget.Connection { ConnectionKey: var key } && key == expectedKey);

        // AppTablesSlot.BoundConnection resolves the connection key from IInstalls on both TestOS and Silo.
        using (ada.EnterAsApp("bookmarks"))
        {
            var resolved = await TablesSlot.BoundConnection(os.Runtime.Services, new TableOwner(ada.Brain.Value, appRef.Key));
            Assert.Equal(expectedKey, resolved);

            var resolvedByComponent = await TablesSlot.BoundConnection(os.Runtime.Services, new TableOwner(ada.Brain.Value, "app.bookmarks"));
            Assert.Equal(expectedKey, resolvedByComponent);
        }
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ConfigureWritesTypedBindingsToInstallRecord(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, f => f.Postgres(), ct);
        var ada = os.Person("ada");
        var appRef = HostAppRef.Of(ada.Brain, "notes");
        var app = ada.Get<IApp>(appRef.Neuron);

        await app.Create(
            new AppManifest("Notes", "Personal notes", [], [], [new AppSlot("tables", "postgres")]),
            "Notes app");

        await app.Configure(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tables"] = "c2" });

        var expectedKey = $"{ada.Brain.Value}/connection/postgres/c2";
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        var record = await installs.Describe("app.notes", ct);
        Assert.NotNull(record);
        Assert.NotNull(record.Typed);
        Assert.Contains(
            record.Typed,
            b => b.Slot == "tables" && b.Target is BindingTarget.Connection { ConnectionKey: var key } && key == expectedKey);

        using (ada.EnterAsApp("notes"))
        {
            var resolved = await TablesSlot.BoundConnection(os.Runtime.Services, new TableOwner(ada.Brain.Value, appRef.Key));
            Assert.Equal(expectedKey, resolved);
        }
    }
}
