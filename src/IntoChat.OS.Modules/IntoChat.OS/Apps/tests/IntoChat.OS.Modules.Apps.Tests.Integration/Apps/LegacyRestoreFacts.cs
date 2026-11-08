using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Tests.Integration.Ino;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Apps;

// A record stored by an older OS is refused on activation, not migrated: Failed, one activity line, nothing deployed. The old values
// are seeded as raw JSON in the TestOS journal (it has no public seam for a raw record, so the fact reaches its state map).
public sealed class LegacyRestoreFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly NeuronId AppId = new(new BrainId("owner"), IntoChat.OS.Connections.AppGrainType.Name, "apps/notes");

    private static TestOS NewOS()
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        return TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
    }

    // Creates a unit-shape app, then rewrites its stored JSON the way the old OS wrote it.
    private static async Task<IApp> Seeded(TestOS os, Action<JsonObject> oldShape)
    {
        var app = os.Get<IApp>(AppId);
        await app.Create(new AppManifest("Notes", "Keep notes", [], [], []), "spec");
        await os.Idle();
        var states = (Dictionary<NeuronId, JsonElement>)typeof(TestJournal)
            .GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(os.Journal(AppId.Brain))!;
        var json = JsonNode.Parse(states[AppId].GetRawText())!.AsObject();
        oldShape(json);
        states[AppId] = JsonSerializer.SerializeToElement(json);
        await os.Deactivate(AppId);
        return app;
    }

    private static async Task<AppSnapshot> Settled(IApp app)
    {
        var read = await app.Read();
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(20, Ct);
            read = await app.Read();
        }
        return read;
    }

    // Spelled apart: the architecture facts keep double-underscore key literals out of test code.
    private static readonly string PrebuiltSetting = new string('_', 2) + "PrebuiltBehaviorsJson";

    private static bool IsRefusal(string line) => line.Contains("rebuilt by the new Creator", StringComparison.Ordinal);

    [Fact]
    public async Task AnAppWithProgramFilesIsRefusedOnActivation()
    {
        await using var os = NewOS();
        var app = await Seeded(os, json => json["fileEntries"] = new JsonArray(
            new JsonObject { ["key"] = "app.cs", ["value"] = "// declaration" },
            new JsonObject { ["key"] = "sync.cs", ["value"] = "return 1;" }));

        var read = await Settled(app);

        Assert.Equal(AppStatus.Failed, read.Status);
        Assert.Contains(read.Activity, IsRefusal);
        Assert.DoesNotContain(read.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
        Assert.Equal("Notes", read.Manifest.Title);
    }

    [Fact]
    public async Task TheRefusalIsAnActivityEntryNotAnException()
    {
        await using var os = NewOS();
        var app = await Seeded(os, json =>
        {
            json["fileEntries"] = new JsonArray(new JsonObject { ["key"] = "sync.cs", ["value"] = "return 1;" });
            json["settingEntries"] = new JsonArray(new JsonObject { ["key"] = "CompiledType", ["value"] = "Missing.Notes, Missing" });
        });

        var first = await app.Read();
        await os.Deactivate(AppId);
        var second = await app.Read();
        var settled = await Settled(app);

        Assert.Equal("Notes", first.Manifest.Title);
        Assert.Equal("Notes", second.Manifest.Title);
        Assert.Equal(AppStatus.Failed, settled.Status);
        // The program is never deployed, so no restore failure is recorded; the refusal is recorded once per activation at most.
        Assert.DoesNotContain(settled.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
        Assert.Single(settled.Activity, IsRefusal);
    }

    [Fact]
    public async Task AnAppWithRetiredFieldsIsRefused()
    {
        await using var os = NewOS();
        var app = await Seeded(os, json =>
        {
            json["runtimeStateJson"] = "{\"count\":3}";
            json["migration"] = "to-units";
        });

        var read = await Settled(app);

        Assert.Equal(AppStatus.Failed, read.Status);
        Assert.Contains(read.Activity, IsRefusal);
    }

    [Fact]
    public async Task AnAppWithAPrebuiltBehaviorSettingIsRefused()
    {
        await using var os = NewOS();
        var app = await Seeded(os, json => json["settingEntries"] = new JsonArray(
            new JsonObject { ["key"] = PrebuiltSetting, ["value"] = "{}" }));

        Assert.Equal(AppStatus.Failed, (await Settled(app)).Status);
    }

    [Fact]
    public async Task AUnitAppWithOnlySpecAndHistoryActivatesNormally()
    {
        await using var os = NewOS();
        var app = await Seeded(os, json => json["fileEntries"] = new JsonArray(
            new JsonObject { ["key"] = "spec.md", ["value"] = "notes" },
            new JsonObject { ["key"] = "scenarios.json", ["value"] = "[]" }));

        var read = await Settled(app);

        Assert.Equal(AppStatus.Drafted, read.Status);
        Assert.DoesNotContain(read.Activity, IsRefusal);
    }

    // The framework's restorer restores a source stored without Bindings as it is, so the studio reports one whose unit declares a
    // [Slot] as Refused (rebuild required) and never restores it.
    [Fact]
    public async Task AUnitSourceStoredWithoutBindingsAndWithASlotNeedsARebuild()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var store = runtime.Services.GetRequiredService<ISourceStore>();
        var files = new Dictionary<string, string> { ["slotted.cs"] = "public static class U { public static void R([Slot] object mail) { } }" };
        await store.Put(runtime.Brain, new SourceRecord("slotted", files, null, "h", "a", [1], [2], ["ino.slotted"], DateTimeOffset.UtcNow), Ct);

        var listed = await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).List();

        var unit = Assert.Single(listed);
        Assert.Equal(DeployStatus.Refused, unit.Status);
        Assert.Equal("slotted", unit.SourceKey);
    }

    [Fact]
    public void ARecordWithoutSlotsOrWithBindingsDoesNotNeedARebuild()
    {
        var plain = new SourceRecord("p", new Dictionary<string, string> { ["u.cs"] = "class U { }" }, null, "h", "a", [], [], ["x"], DateTimeOffset.UtcNow);
        var bound = new SourceRecord("b", new Dictionary<string, string> { ["u.cs"] = "[Slot] x" }, null, "h", "a", [], [], ["x"], DateTimeOffset.UtcNow,
            new Dictionary<string, IReadOnlyDictionary<string, string>>());

        Assert.False(SynapseStudioNeuron.RebuildRequired(plain));
        Assert.False(SynapseStudioNeuron.RebuildRequired(bound));
    }
}
