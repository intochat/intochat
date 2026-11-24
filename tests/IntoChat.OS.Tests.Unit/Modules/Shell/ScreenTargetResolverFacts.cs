using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Screens;

namespace IntoChat.OS.Tests.Unit.Modules.Shell;

public sealed class ScreenTargetResolverFacts
{
    private static readonly BrainId Brain = new("b1");

    [Fact]
    public async Task ResolvesConfirmationsCompiledScreenAgentAppAndNonAgentApp()
    {
        var agentApp = new StubApp(new AppSnapshot(
            "treg",
            new AppManifest("Treg", "Treg agent", [], [], [], Agent: new AppAgent("Be treg.")),
            "",
            new(),
            new(),
            new(),
            "1.0.0",
            AppStatus.Live,
            [],
            []));

        var plainApp = new StubApp(new AppSnapshot(
            "plain",
            new AppManifest("Plain", "Plain app", [], [], []),
            "",
            new(),
            new(),
            new(),
            "1.0.0",
            AppStatus.Live,
            [],
            []));

        var proxies = new FakeProxies(new Dictionary<NeuronId, object>
        {
            [PlainNeuronContracts.IdOf(typeof(IApp), "b1/apps/treg")] = agentApp,
            [PlainNeuronContracts.IdOf(typeof(IApp), "b1/apps/plain")] = plainApp,
        });

        var hosted = new FakeHostedAppTypes(["ino"]);
        var resolver = new ScreenTargetResolver(proxies, [hosted]);

        var ct = TestContext.Current.CancellationToken;
        var confirmations = await resolver.ResolveAsync(Brain, "confirmations", ct);
        Assert.NotNull(confirmations);
        Assert.Equal(IScreenTargetResolver.ConfirmationsKind, confirmations.Kind);
        Assert.Equal(new NeuronId(Brain, "confirmations", "default"), confirmations.ScreenId);

        var compiled = await resolver.ResolveAsync(Brain, "ino", ct);
        Assert.NotNull(compiled);
        Assert.Equal(IScreenTargetResolver.CompiledScreenKind, compiled.Kind);
        Assert.Equal(new NeuronId(Brain, "app.ino", "default"), compiled.ScreenId);

        var agent = await resolver.ResolveAsync(Brain, "treg", ct);
        Assert.NotNull(agent);
        Assert.Equal(IScreenTargetResolver.AgentAppKind, agent.Kind);
        Assert.Equal(new NeuronId(Brain, "agentapp", "treg"), agent.ScreenId);

        var agentByFullKey = await resolver.ResolveAsync(Brain, "b1/apps/treg/view", ct);
        Assert.Equal(agent, agentByFullKey);

        var plain = await resolver.ResolveAsync(Brain, "plain", ct);
        Assert.Null(plain);
    }

    private sealed class FakeHostedAppTypes(IEnumerable<string> loadedNames) : IHostedAppTypes
    {
        private readonly HashSet<string> _loaded = new(loadedNames, StringComparer.Ordinal);

        public bool IsHostLoaded(string appKey)
        {
            var name = IntoChat.OS.Host.Primitives.AppRef.TryParse(appKey, out var app) ? app.Name : appKey;
            return _loaded.Contains(name);
        }
    }

    private sealed class FakeProxies(Dictionary<NeuronId, object> map) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class
            => map.TryGetValue(id, out var instance)
                ? (T)instance
                : throw new InvalidOperationException($"No fake neuron registered for {id}");
    }

    private sealed class StubApp(AppSnapshot snapshot) : IApp
    {
        public Task<AppSnapshot> Read() => Task.FromResult(snapshot);
        public Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation) => throw new NotSupportedException();
        public Task<AppSnapshot> Create(AppManifest manifest, string spec) => throw new NotSupportedException();
        public Task<AppSnapshot> SaveSpec(string spec, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections) => throw new NotSupportedException();
        public Task<SlotBindingOutcome> BindSlots(Dictionary<string, string> bindings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppSnapshot> Rollback(string version) => throw new NotSupportedException();
        public Task<AppSnapshot> BeginBuild(string? instruction) => throw new NotSupportedException();
        public Task<Guid> CurrentBuildAttempt() => throw new NotSupportedException();
        public Task<string?> BuildFlow() => throw new NotSupportedException();
        public Task<AppSnapshot> CompleteUnitBuild(Guid attempt, string hash, DigitalBrain.Units.InstallStatus status, string sentence) => throw new NotSupportedException();
        public Task Progress(string state, string detail) => throw new NotSupportedException();
        public Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null) => throw new NotSupportedException();
        public Task<AppBundle> Export() => throw new NotSupportedException();
        public Task<AppSnapshot> Publish() => throw new NotSupportedException();
        public Task<AppBundle> ExportPublished() => throw new NotSupportedException();
        public Task<AppBundle> ExportRelease(string version, string digest) => throw new NotSupportedException();
        public Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null) => throw new NotSupportedException();
        public Task<AppSnapshot> Replace(AppBundle bundle, AppOrigin? origin = null) => throw new NotSupportedException();
        public Task Delete() => throw new NotSupportedException();
        public Task<Guid> Request(string operation, string input) => throw new NotSupportedException();
        public Task<Guid> Emit(string signal, string args) => throw new NotSupportedException();
    }
}
