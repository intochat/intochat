using DigitalBrain;
using global::Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Tasks;

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
    private sealed class RecordingTaskEngine : ITaskEngine
    {
        public List<CreateTaskRequest> Created { get; } = [];

        public Task<AgentTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
        {
            Created.Add(request);
            return Task.FromResult(new AgentTask("t-1", request.Title, request.Description, request.Owner, IntoChat.OS.Modules.AI.Tasks.TaskStatus.Pending, [], DateTimeOffset.UnixEpoch));
        }

        public Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentTask>> ListTasksAsync(string? owner = null, IntoChat.OS.Modules.AI.Tasks.TaskStatus? status = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentTask> AdvanceTaskStepAsync(string taskId, int stepIndex, TaskStepStatus newStatus, string? output = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentTask> RollbackTaskAsync(string taskId, int toStepIndex, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(string taskId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TaskVerificationResult> VerifyTaskStepAsync(string taskId, int stepIndex, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentTask> ConfirmTaskStepAsync(string taskId, int stepIndex, string? confirmationToken = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentTask> LinkTaskAsync(string taskId, string targetId, string kind, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static IInoData Data(TestOS os) => os.Get<IInoData>(new NeuronId(Brain, "ino.data", "ino/data"));

    [Fact]
    public async Task MakeReturnsAPendingApprovalCardAndCreatesATask()
    {
        var engine = new RecordingTaskEngine();
        using var services = new global::Microsoft.Extensions.DependencyInjection.ServiceCollection().AddOptions<AppsOptions>().Services.AddSingleton(TimeProvider.System).AddSingleton<ITaskEngine>(engine).BuildServiceProvider();
        await using var os = NewOs(services);

        var result = await os.Get<IInoStudio>(new NeuronId(Brain, "ino.studio", "ino/studio")).Make("Draft the Q4 plan");

        var request = Assert.Single(engine.Created);
        Assert.Equal("Draft the Q4 plan", request.Title);
        Assert.Equal("User", request.Owner);
        Assert.True(result.Card is not null, result.Text);
        Assert.Equal("approval", result.Card.Kind);
        Assert.Equal("pending", result.Card.Status);
        Assert.Contains("t-1", result.Text);
    }

    [Fact]
    public async Task MakeWithoutATaskEngineSaysSoAndHasNoCard()
    {
        await using var os = NewOs();
        var result = await os.Get<IInoStudio>(new NeuronId(Brain, "ino.studio", "ino/studio")).Make("anything");
        Assert.Null(result.Card);
        Assert.Contains("Task engine is unavailable", result.Text);
    }

    [Fact]
    public async Task ListNamesTheStudios()
    {
        await using var os = NewOs();
        var result = await os.Get<IInoStudio>(new NeuronId(Brain, "ino.studio", "ino/studio")).List();
        Assert.Contains("markdraw", result.Text);
    }

    [Fact]
    public async Task ConnectThenQueryReturnsRows()
    {
        await using var os = NewOs();
        var connected = await Data(os).Connect("Host=db.example;Database=sales");
        Assert.Equal("db-connection", connected.Card!.Kind);

        var queried = await Data(os).Query("SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5");
        Assert.Equal("table", queried.Card!.Kind);
        Assert.Equal(5, queried.Card.Rows!.Length);
        Assert.Equal("Opportunity", queried.Card.Columns![0]);
        Assert.Contains("sales", queried.Card.Title);

        var state = await Data(os).Read();
        Assert.Equal("main:db.example/sales", state.ConnectedDatabase);
        Assert.Single(state.Charts);
    }

    [Fact]
    public async Task ChartReturnsAChartCard()
    {
        await using var os = NewOs();
        var result = await Data(os).Chart("show a pie chart of opportunities");
        Assert.Equal("chart", result.Card!.Kind);
        Assert.Equal("pie", result.Card.ChartKind);
        Assert.NotEmpty(result.Card.ChartPoints!);
        Assert.Equal(result.Card.Id, Assert.Single((await Data(os).Read()).Charts).Id);
    }
}
