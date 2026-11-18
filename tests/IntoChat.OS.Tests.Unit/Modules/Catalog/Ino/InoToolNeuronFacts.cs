using DigitalBrain.Access;
using DigitalBrain;
using DigitalBrain.Testing;
using global::Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InoToolNeuronFacts
{
    private static readonly BrainId Brain = new("owner");

    private static TestOS NewOs(IServiceProvider? services = null)
    {
        var sp = services ?? new ServiceCollection().BuildServiceProvider();
        var os = TestOS.Local(options => options.Services = sp).Register<AppNeuron>();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        return os;
    }

    private static IInoSettings Settings(TestOS os) => os.Get<IInoSettings>(new NeuronId(Brain, "ino.settings", "ino/settings"));

    private static IAgentContext Inference(TestOS os) => os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/conversation.inference"));

    [Fact]
    public async Task NavigateReturnsASettingsCard()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        using var services = new global::Microsoft.Extensions.DependencyInjection.ServiceCollection().AddOptions<AppsOptions>().Services.AddSingleton(TimeProvider.System).BuildServiceProvider();
        await using var os = NewOs(services);
        await os.Get<IApp>(new NeuronId(Brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/settings"))
            .Create(new AppManifest("Settings", "Settings", [new AppOperation("navigate", "Open a settings page")], [], []), "spec");
        var result = await Settings(os).Navigate("models", "local-server");
        Assert.True(result.Card is not null, result.Text);
        Assert.Equal("settings", result.Card.Kind);
        Assert.Equal("neuron://owner/apps/settings", result.Card.SourceNeuronUri);
        Assert.Equal("Settings · models / local-server", result.Card.Title);
    }

    [Fact]
    public async Task SetModelWritesAnInferenceEntry()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        await using var os = NewOs();
        await Settings(os).SetModel("x");
        var entry = Assert.Single((await Inference(os).Read()).Entries);
        Assert.Contains(ContextTags.Inference, entry.Tags!);
        Assert.Equal("""{"model":"x"}""", entry.Text);
    }

    [Fact]
    public async Task SettingsChangedRewritesUserProfile()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        await using var os = NewOs();
        var profile = os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/user.profile"));
        await os.Get<IProfileContextWriter>(new NeuronId(Brain, "ino.profile-writer", "ino/profile-writer")).Ensure();
        var settings = os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings"));

        await settings.SetProfile("Ada", "dark", "model-a");
        await settings.SetPreferences(language: "Czech", timeZone: "Europe/Prague", dateFormat: "2026-10-17");
        await os.Idle();
        var entries = (await profile.Read()).Entries;
        var text = Assert.Single(entries, entry => entry.Name == "profile").Text;
        Assert.Contains("Ada", text);
        Assert.Contains("Czech", text);
        Assert.Contains("Europe/Prague", text);
        Assert.Contains("2026-10-17", text);
        Assert.DoesNotContain(entries, entry => entry.Name == "inference");
    }

    [Fact]
    public async Task SetTemperatureKeepsTheModel()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        await using var os = NewOs();
        await Settings(os).SetModel("x");
        await Settings(os).SetTemperature(0.5f);
        var entry = Assert.Single((await Inference(os).Read()).Entries);
        Assert.Equal("""{"model":"x","temperature":0.5}""", entry.Text);
    }

    [Fact]
    public async Task SettingsChangedDoesNotOverwriteConversationInferenceModel()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        await using var os = NewOs();
        var profile = os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/user.profile"));
        await os.Get<IProfileContextWriter>(new NeuronId(Brain, "ino.profile-writer", "ino/profile-writer")).Ensure();
        var userSettings = os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings"));

        // 1. Set explicit turn model & temperature via IInoSettings (writes to context/conversation.inference)
        await Settings(os).SetModel("qwen3.5:4b");
        await Settings(os).SetTemperature(0.4f);
        await os.Idle();
        Assert.Equal("""{"model":"qwen3.5:4b","temperature":0.4}""", Assert.Single((await Inference(os).Read()).Entries).Text);

        // 2. Switch user profile model via IUserSettings.SetProfile (e.g. from Settings UI or /model command)
        await userSettings.SetProfile("Ada", "dark", "claude-sonnet-4-6");
        await os.Idle();

        // ProfileContextWriter no longer writes inference entries into user.profile and never overwrites conversation.inference.
        Assert.DoesNotContain(await profile.Read() is var p ? p.Entries : [], e => e.Name == "inference");
        Assert.Equal("""{"model":"qwen3.5:4b","temperature":0.4}""", Assert.Single((await Inference(os).Read()).Entries).Text);
    }

    [Fact]
    public async Task SetVariableStoresItInUserSettings()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        await using var os = NewOs();
        await Settings(os).SetVariable("region", "eu");
        var state = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
        Assert.Contains(state.Variables, v => v.Key == "region" && v.Value == "eu");
    }

    [Fact]
    public async Task OpenReadsAFileTheStorageHolds()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
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
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        await using var os = NewOs();
        var result = await Settings(os).Navigate("models");
        Assert.Null(result.Card);
        Assert.Contains("Settings is unavailable", result.Text);
    }

    [Fact]
    public async Task FindReturnsMatches()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
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
    private static IInoData Data(TestOS os) => os.Get<IInoData>(new NeuronId(Brain, "ino.data", "ino/data"));

    // Without a configured model/provider, Make must fail honestly with a Settings setup card rather than synthesizing a fake unit.
    [Fact]
    public async Task MakeWithoutModelFailsHonestlyWithSettingsSetupCard()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        await using var os = NewOs();

        var result = await os.Get<IInoStudio>(new NeuronId(Brain, "ino.studio", "ino/studio")).Make("Draft the Q4 plan");

        Assert.False(result.Ok);
        Assert.NotNull(result.Card);
        Assert.Equal("settings", result.Card!.Kind);
        Assert.Equal("setup-model", result.Card.Id);
        Assert.Equal("neuron://owner/apps/settings", result.Card.SourceNeuronUri);
    }

    [Fact]
    public async Task ListNamesTheStudios()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        using var services = new global::Microsoft.Extensions.DependencyInjection.ServiceCollection().AddOptions<AppsOptions>().Services
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new FeatureCatalog([typeof(AppsModule).Assembly, typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly])).BuildServiceProvider();
        await using var os = NewOs(services);
        var result = await os.Get<IInoStudio>(new NeuronId(Brain, "ino.studio", "ino/studio")).List();
        Assert.Contains("- markdraw: ", result.Text);
        Assert.Contains("- photocraft: ", result.Text);
        Assert.DoesNotContain("- creator: ", result.Text);
    }

    private static DigitalBrain.Testing.Conformance.Storage.InMemoryStorage CreateOpportunitiesStorage()
    {
        var storage = new DigitalBrain.Testing.Conformance.Storage.InMemoryStorage("Documents");
        storage.AddFile(storage.Root, "opportunities.csv", """
Opportunity,Account,Stage,Amount
Nordkapp second region,Nordkapp Shipping,Discovery,74500
Cold-chain sensors,Lofoten Seafood,Proposal,31200
Deep-sea mooring telemetry,Bergen Offshore,Won,120000
Autonomous ferry dispatch,Stavanger Mobility,Qualified,45000
Subsea cabling condition,Trondheim Subsea,Won,95000
"""u8.ToArray());
        return storage;
    }

    [Fact]
    public async Task ConnectRefusesFakePostgresPretense()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        var storage = CreateOpportunitiesStorage();
        await using var os = NewOs().With<DigitalBrain.Capabilities.Storage.IStorage>("documents", storage);

        var connected = await Data(os).Connect("Host=db.example;Database=sales");

        Assert.False(connected.Ok);
        Assert.Null(connected.Card);
        Assert.DoesNotContain("PostgreSQL Connected", connected.Text, StringComparison.OrdinalIgnoreCase);

        var state = await Data(os).Read();
        Assert.Null(state.ConnectedDatabase);
    }

    [Fact]
    public async Task QueryWorkspaceFileReturnsRowsAndRefusesUnknownTable()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        var storage = CreateOpportunitiesStorage();
        await using var os = NewOs().With<DigitalBrain.Capabilities.Storage.IStorage>("documents", storage);

        var queried = await Data(os).Query("SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5");
        Assert.True(queried.Ok);
        Assert.Equal("table", queried.Card!.Kind);
        Assert.Equal(5, queried.Card.Rows!.Length);
        Assert.Equal("Opportunity", queried.Card.Columns![0]);

        var missing = await Data(os).Query("SELECT account, stage FROM unbacked_postgres_table");
        Assert.False(missing.Ok);
        Assert.Null(missing.Card);

        var state = await Data(os).Read();
        Assert.Null(state.ConnectedDatabase);
        Assert.Single(state.Charts);
    }

    [Fact]
    public async Task ChartReturnsAChartCard()
    {
        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("owner", "owner"), new BrainId("owner")));
        var storage = CreateOpportunitiesStorage();
        await using var os = NewOs().With<DigitalBrain.Capabilities.Storage.IStorage>("documents", storage);
        var result = await Data(os).Chart("show a pie chart of opportunities");
        Assert.Equal("chart", result.Card!.Kind);
        Assert.Equal("pie", result.Card.ChartKind);
        Assert.NotEmpty(result.Card.ChartPoints!);
        Assert.Equal(result.Card.Id, Assert.Single((await Data(os).Read()).Charts).Id);
    }
}
