using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Modules.Apps;

public sealed class AppsModuleOnTestOS : ModuleOnTestOSFacts<AppsModule>
{
    [Fact]
    public void TraceReaderRedactsBearerAndApiKeysInPayloads()
    {
        var raw = """{"auth":"Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9","openai":"sk-proj-1234567890abcdef","github":"ghp_9876543210abcdef","safe":"hello"}""";
        var redacted = TraceReaderNeuron.Redact(raw);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-proj-1234567890abcdef", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_9876543210abcdef", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("\"safe\":\"hello\"", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortableBrainArchiveRoundTripsSettingsProfileAndUnitsThroughJson()
    {
        await using var sourceOs = TestOS.Local();
        sourceOs.Register<UserSettingsNeuron>();
        sourceOs.Register<ProfileNeuron>();
        sourceOs.Register<InMemoryStudio>();

        var srcSettings = sourceOs.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        var srcProfile = sourceOs.Get<IProfile>(new NeuronId(new BrainId("local"), "os.profile", "profile"));
        var srcStudio = sourceOs.Get<ISynapseStudio>(ISynapseStudio.Key("local"));

        await srcSettings.SetProfile("Ada", "dark", "gpt-5.4");
        await srcSettings.SetVariable("ino.smart", "on");
        await srcProfile.UpdateProfile("ada", "Ada Lovelace", "Analytical Engine", ["creator"]);
        await srcProfile.AddFriend("babbage", "charles", "Charles Babbage", "friends");
        await srcStudio.DeployFiles("watch.web", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WebWatch.cs"] = "// unit code",
            ["spec.md"] = "# Web Watch",
        }, new Dictionary<string, string>(StringComparer.Ordinal));

        var exported = await PortableBrainArchive.ExportAsync("local", srcSettings, srcProfile, srcStudio);
        var json = PortableBrainArchive.ToJson(exported);
        var parsed = PortableBrainArchive.FromJson(json);

        await using var destOs = TestOS.Local();
        destOs.Register<UserSettingsNeuron>();
        destOs.Register<ProfileNeuron>();
        destOs.Register<InMemoryStudio>();

        var dstSettings = destOs.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        var dstProfile = destOs.Get<IProfile>(new NeuronId(new BrainId("local"), "os.profile", "profile"));
        var dstStudio = destOs.Get<ISynapseStudio>(ISynapseStudio.Key("local"));

        await PortableBrainArchive.ImportAsync(parsed, dstSettings, dstProfile, dstStudio);

        var restoredSettings = await dstSettings.Read();
        Assert.Equal("Ada", restoredSettings.DisplayName);
        Assert.Equal("gpt-5.4", restoredSettings.DefaultModel);
        Assert.Contains(restoredSettings.Variables, v => v.Key == "ino.smart" && v.Value == "on");

        var restoredProfile = await dstProfile.Read();
        Assert.Equal("ada", restoredProfile.Handle);
        Assert.Contains(restoredProfile.Friends ?? [], f => f.BrainId == "babbage");

        var restoredFiles = await dstStudio.SourceFiles("watch.web");
        Assert.Equal("// unit code", restoredFiles["WebWatch.cs"]);
    }

    [Fact]
    public async Task BrainSnapshotArchiveRoundTripsSettingsProfileAndUnitsThroughJson()
    {
        await using var sourceOs = TestOS.Local();
        sourceOs.Register<UserSettingsNeuron>();
        sourceOs.Register<ProfileNeuron>();
        sourceOs.Register<InMemoryStudio>();

        var srcSettings = sourceOs.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        var srcProfile = sourceOs.Get<IProfile>(new NeuronId(new BrainId("local"), "os.profile", "profile"));
        var srcStudio = sourceOs.Get<ISynapseStudio>(ISynapseStudio.Key("local"));

        await srcSettings.SetProfile("Alan", "light", "claude-3.7");
        await srcProfile.UpdateProfile("alan", "Alan Turing", "Universal Machine", ["creator"]);

        var exported = await BrainSnapshotArchive.ExportAsync("local", srcSettings, srcProfile, srcStudio);
        var json = BrainSnapshotArchive.ToJson(exported);
        var parsed = BrainSnapshotArchive.FromJson(json);

        await using var destOs = TestOS.Local();
        destOs.Register<UserSettingsNeuron>();
        destOs.Register<ProfileNeuron>();
        destOs.Register<InMemoryStudio>();

        var dstSettings = destOs.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        var dstProfile = destOs.Get<IProfile>(new NeuronId(new BrainId("local"), "os.profile", "profile"));
        var dstStudio = destOs.Get<ISynapseStudio>(ISynapseStudio.Key("local"));

        await BrainSnapshotArchive.ImportAsync(parsed, dstSettings, dstProfile, dstStudio);

        var restoredSettings = await dstSettings.Read();
        Assert.Equal("Alan", restoredSettings.DisplayName);
        Assert.Equal("claude-3.7", restoredSettings.DefaultModel);

        var restoredProfile = await dstProfile.Read();
        Assert.Equal("alan", restoredProfile.Handle);
    }

    [Neuron("ino.synapses")]
    private sealed class InMemoryStudio : Neuron, ISynapseStudio
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _sidecars = new(StringComparer.Ordinal);

        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
        {
            _sources[alias] = files.Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            _sidecars[alias] = files.Where(kv => !kv.Key.EndsWith(".cs", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return Task.FromResult(new UnitDeployResult(true, "ok", DeployStatus.Deployed, "h1", new Dictionary<string, DigitalBrain.Units.InstallOutcome>()));
        }
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult("ok"));
        public Task<DeployedSource[]> List()
            => Task.FromResult(_sources.Keys.Select(k => new DeployedSource(k, "h1", [k], DeployStatus.Deployed, null, DateTimeOffset.UtcNow)).ToArray());
        public Task<Dictionary<string, string>> Sidecars(string alias)
            => Task.FromResult(_sidecars.GetValueOrDefault(alias) ?? new Dictionary<string, string>(StringComparer.Ordinal));
        public Task<Dictionary<string, string>> SourceFiles(string alias)
            => Task.FromResult(_sources.GetValueOrDefault(alias) ?? new Dictionary<string, string>(StringComparer.Ordinal));
        public Task<InoToolResult> Failures(string alias)
            => Task.FromResult(new InoToolResult($"{alias} has no recorded failures.", Ok: true));
    }
}
