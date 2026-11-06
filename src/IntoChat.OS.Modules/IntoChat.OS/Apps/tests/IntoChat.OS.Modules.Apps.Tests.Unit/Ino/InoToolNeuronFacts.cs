using DigitalBrain;
using global::Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InoToolNeuronFacts
{
    private static readonly BrainId Brain = new("local");

    private static TestOS NewOs(IServiceProvider? services = null)
    {
        var os = services is null ? TestOS.Local() : TestOS.Local(options => options.Services = services).Register<AppNeuron>();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        return os;
    }

    private static IInoSettings Settings(TestOS os) => os.Get<IInoSettings>(new NeuronId(Brain, "ino.settings", "ino/settings"));

    private static IAgentContext Inference(TestOS os) => os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/conversation.inference"));

    [Fact]
    public async Task NavigateReturnsASettingsCard()
    {
        using var services = new global::Microsoft.Extensions.DependencyInjection.ServiceCollection().AddOptions<AppsOptions>().Services.AddSingleton(TimeProvider.System).BuildServiceProvider();
        await using var os = NewOs(services);
        await os.Get<IApp>(new NeuronId(Brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/settings"))
            .Create(new AppManifest("Settings", "Settings", [new AppOperation("navigate", "Open a settings page")], [], []), "spec");
        var result = await Settings(os).Navigate("models", "local-server");
        Assert.True(result.Card is not null, result.Text);
        Assert.Equal("settings", result.Card.Kind);
        Assert.Equal("neuron://local/apps/settings", result.Card.SourceNeuronUri);
        Assert.Equal("Settings · models / local-server", result.Card.Title);
    }

    [Fact]
    public async Task SetModelWritesAnInferenceEntry()
    {
        await using var os = NewOs();
        await Settings(os).SetModel("x");
        var entry = Assert.Single((await Inference(os).Read()).Entries);
        Assert.Contains(ContextTags.Inference, entry.Tags!);
        Assert.Equal("""{"model":"x"}""", entry.Text);
    }

    [Fact]
    public async Task SetTemperatureKeepsTheModel()
    {
        await using var os = NewOs();
        await Settings(os).SetModel("x");
        await Settings(os).SetTemperature(0.5f);
        var entry = Assert.Single((await Inference(os).Read()).Entries);
        Assert.Equal("""{"model":"x","temperature":0.5}""", entry.Text);
    }

    [Fact]
    public async Task SetVariableStoresItInUserSettings()
    {
        await using var os = NewOs();
        await Settings(os).SetVariable("region", "eu");
        var state = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
        Assert.Contains(state.Variables, v => v.Key == "region" && v.Value == "eu");
    }

    [Fact]
    public async Task OpenReadsAFileTheStorageHolds()
    {
        var storage = new DigitalBrain.Testing.Conformance.Storage.InMemoryStorage("Documents");
        storage.AddFile(storage.Root, "notes.md", "hello from storage"u8.ToArray());
        await using var os = NewOs().With<DigitalBrain.Capabilities.Storage.IStorage>("documents", storage);
        var files = os.Get<IInoFiles>(new NeuronId(Brain, "ino.files", "ino/files"));

        var opened = await files.Open("notes.md");
        Assert.Contains("notes.md", opened.Text);
        Assert.Equal("hello from storage", opened.Card!.Summary);
        Assert.Contains("hello from storage", (await files.Summarize("notes.md")).Text);
    }

    [Fact]
    public async Task NavigateWithoutASettingsAppSaysSoAndHasNoCard()
    {
        await using var os = NewOs();
        var result = await Settings(os).Navigate("models");
        Assert.Null(result.Card);
        Assert.Contains("Settings is unavailable", result.Text);
    }

    [Fact]
    public async Task FindReturnsMatches()
    {
        var storage = new DigitalBrain.Testing.Conformance.Storage.InMemoryStorage("Documents");
        storage.AddFile(storage.Root, "roadmap.md", "ship it"u8.ToArray());
        await using var os = NewOs().With<DigitalBrain.Capabilities.Storage.IStorage>("documents", storage);
        var files = os.Get<IInoFiles>(new NeuronId(Brain, "ino.files", "ino/files"));

        var hit = await files.Find("roadmap");
        Assert.Contains("roadmap.md", hit.Text);
        Assert.Equal("file", hit.Card!.Kind);

        var miss = await files.Find("no-such-file.md");
        Assert.Contains("No files match", miss.Text);
    }
}
