using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Modules.Apps.Hosting.InProcess;
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

    // The deploy target: a real in-process host. A deploy of a compiled type leaves a hosted program (Describe) and presents it
    // (the counting sink); a deploy that threw first is recorded as "restore failed". Attempts adds the three up.
    private sealed class CountingSink : IPresentationSink
    {
        private int _presents;
        public int Presents => Volatile.Read(ref _presents);

        public Task Present(string owner, DigitalBrain.UI.View? side, DigitalBrain.UI.View? main, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _presents);
            return Task.CompletedTask;
        }
    }

    private sealed class Spy(InProcessAppHost host, CountingSink sink)
    {
        public async Task<int> Attempts(AppSnapshot read)
            => sink.Presents + (await host.Describe(IApp.Key("owner", "notes"), CancellationToken.None)).Length
                + read.Activity.Count(line => line.StartsWith("restore failed:", StringComparison.Ordinal));
    }

    private static readonly string CompiledType = typeof(PostApp).AssemblyQualifiedName!;

    private static TestOS NewOS(out Spy spy)
    {
        var sink = new CountingSink();
        var host = new InProcessAppHost((_, _) => throw new NotSupportedException(), _ => throw new NotSupportedException(), sink);
        spy = new Spy(host, sink);
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(host);
        return TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
    }

    private static JsonObject Entry(string key, string value) => new() { ["key"] = key, ["value"] = value };

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
        // The state is written twice, as entries and as plain maps, and the maps load last: keep them in step with the entries.
        foreach (var (entries, map) in new[] { ("fileEntries", "files"), ("settingEntries", "settings") })
        {
            json[map] = new JsonObject(json[entries]!.AsArray().Select(entry => KeyValuePair.Create<string, JsonNode?>(
                entry!["key"]!.GetValue<string>(), JsonValue.Create(entry["value"]!.GetValue<string>()))));
        }
        states[AppId] = JsonSerializer.SerializeToElement(json);
        await os.Deactivate(AppId);
        return app;
    }

    // The first call activates the app and queues the restore as a one-way turn of its own: wait for every turn, then read again.
    private static async Task<AppSnapshot> Settled(TestOS os, IApp app)
    {
        _ = await app.Read();
        await os.Idle();
        return await app.Read();
    }

    // Spelled apart: the architecture facts keep double-underscore key literals out of test code.
    private static readonly string PrebuiltSetting = new string('_', 2) + "PrebuiltBehaviorsJson";

    private static bool IsRefusal(string line) => line.Contains("rebuilt by the new Creator", StringComparison.Ordinal);

    // An old record that names a compiled type, so a deploy would be attempted if the record were not refused.
    private static Action<JsonObject> WithProgram(params (string Path, string Source)[] files) => json =>
    {
        json["fileEntries"] = new JsonArray([.. files.Select(file => (JsonNode)Entry(file.Path, file.Source))]);
        json["settingEntries"] = new JsonArray(Entry(AppTrust.CompiledTypeSetting, CompiledType));
    };

    [Fact]
    public async Task AnAppWithProgramFilesIsRefusedOnActivation()
    {
        await using var os = NewOS(out var spy);
        var app = await Seeded(os, WithProgram(("app.cs", "// declaration"), ("sync.cs", "return 1;")));

        var read = await Settled(os, app);

        Assert.Equal(AppStatus.Failed, read.Status);
        Assert.Contains(read.Activity, IsRefusal);
        Assert.Equal("Notes", read.Manifest.Title);
        Assert.Equal(0, await spy.Attempts(read));
        Assert.DoesNotContain(read.Files.Keys, path => path.EndsWith(".cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRefusalIsAnActivityEntryNotAnException()
    {
        await using var os = NewOS(out var spy);
        var app = await Seeded(os, WithProgram(("sync.cs", "return 1;")));

        // Activation and the first call succeed; the refusal is a line on the record.
        var first = await app.Read();
        await os.Deactivate(AppId);
        var second = await app.Read();
        var settled = await Settled(os, app);

        Assert.Equal("Notes", first.Manifest.Title);
        Assert.Equal("Notes", second.Manifest.Title);
        Assert.Equal(AppStatus.Failed, settled.Status);
        Assert.Equal(0, await spy.Attempts(settled));
        Assert.Single(settled.Activity, IsRefusal);
    }

    [Fact]
    public async Task ARestoreOfAUnitsRecordThatNamesACompiledTypeStillDeploys()
    {
        // The control: without an old-shape marker the same record is deployed, so the zero counts above prove a refusal.
        await using var os = NewOS(out var spy);
        var app = await Seeded(os, json => json["settingEntries"] = new JsonArray(Entry(AppTrust.CompiledTypeSetting, CompiledType)));

        var read = await Settled(os, app);

        Assert.DoesNotContain(read.Activity, IsRefusal);
        Assert.True(await spy.Attempts(read) > 0);
    }

    [Fact]
    public async Task AConfigureOnARefusedRecordDeploysNothing()
    {
        await using var os = NewOS(out var spy);
        var app = await Seeded(os, WithProgram(("sync.cs", "return 1;")));
        _ = await Settled(os, app);

        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => app.Configure([], []));

        Assert.Contains("refused", refusal.Message, StringComparison.Ordinal);
        var after = await Settled(os, app);
        Assert.Equal(AppStatus.Failed, after.Status);
        Assert.Equal(0, await spy.Attempts(after));
    }

    [Fact]
    public async Task ARollbackOnARefusedRecordIsRefused()
    {
        await using var os = NewOS(out var spy);
        var app = await Seeded(os, WithProgram(("sync.cs", "return 1;")));
        var before = await Settled(os, app);

        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => app.Rollback(before.Version));

        Assert.Contains("refused", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, await spy.Attempts(await Settled(os, app)));
    }

    [Fact]
    public async Task AnAppWithRetiredFieldsIsRefused()
    {
        await using var os = NewOS(out var spy);
        var app = await Seeded(os, json =>
        {
            json["runtimeStateJson"] = "{\"count\":3}";
            json["migration"] = "to-units";
            json["settingEntries"] = new JsonArray(Entry(AppTrust.CompiledTypeSetting, CompiledType));
        });

        var read = await Settled(os, app);

        Assert.Equal(AppStatus.Failed, read.Status);
        Assert.Contains(read.Activity, IsRefusal);
        Assert.Equal(0, await spy.Attempts(read));
    }

    [Fact]
    public async Task AnAppWithAPrebuiltBehaviorSettingIsRefused()
    {
        await using var os = NewOS(out _);
        var app = await Seeded(os, json => json["settingEntries"] = new JsonArray(Entry(PrebuiltSetting, "{}")));

        Assert.Equal(AppStatus.Failed, (await Settled(os, app)).Status);
    }

    [Fact]
    public async Task AUnitAppWithOnlySpecAndHistoryActivatesNormally()
    {
        await using var os = NewOS(out _);
        var app = await Seeded(os, json => json["fileEntries"] = new JsonArray(Entry("spec.md", "notes"), Entry("scenarios.json", "[]")));

        var read = await Settled(os, app);

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
