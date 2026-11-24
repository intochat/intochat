using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Tests;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class AgentToolsOperationsFacts
{
    private static readonly BrainId Brain = new("local");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ComponentManifest AppManifestOf(string appId, string title, params string[] opNames)
        => new(
            Id: new ComponentId($"app.{appId}"),
            Slots: [],
            Face: new ComponentFace(title),
            Operations: [.. opNames.Select(name => new OperationSpec(name, $"Run {name}"))]);

    [Fact]
    public async Task OperationsReturnsOnlyDeclaredOperationsForPassedAppsAndOmitsClosedApps()
    {
        ComponentManifest[] manifests =
        [
            AppManifestOf("alpha", "Alpha", "ping", "reset"),
            AppManifestOf("beta", "Beta", "export"),
            AppManifestOf("gamma", "Gamma", "destroy"),
            new ComponentManifest(
                Id: new ComponentId("module.storage"),
                Slots: [],
                Operations: [new OperationSpec("gc", "Garbage collect")]),
        ];

        await using var os = TestOS.Local();
        var tools = AgentTools.Operations(
            edge: null,
            proxies: os.Proxies,
            conversation: "local/conversations/main",
            appIds: ["alpha", "apps/beta"],
            manifests: manifests);

        var names = tools.Select(static t => t.Name).ToArray();
        Assert.Equal(["alpha_ping", "alpha_reset", "beta_export"], names);
        Assert.DoesNotContain(names, static n => n.Contains("gamma", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static n => n.Contains("storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OperationsReturnsEmptyWhenNoAppsAreScoped()
    {
        ComponentManifest[] manifests =
        [
            AppManifestOf("alpha", "Alpha", "ping"),
            AppManifestOf("beta", "Beta", "export"),
        ];

        await using var os = TestOS.Local();
        Assert.Empty(AgentTools.Operations(null, os.Proxies, "local/conversations/main", [], manifests));
        Assert.Empty(AgentTools.Operations(null, os.Proxies, "local/conversations/main", ["   ", ""], manifests));
    }

    [Fact]
    public async Task OperationsNormalizesAppPathsAndPrefixesCaseInsensitivelyWithoutDuplicates()
    {
        ComponentManifest[] manifests =
        [
            AppManifestOf("alpha", "Alpha", "ping"),
            AppManifestOf("beta", "Beta", "export"),
        ];

        await using var os = TestOS.Local();
        var tools = AgentTools.Operations(
            edge: null,
            proxies: os.Proxies,
            conversation: "local/conversations/main",
            appIds: ["local/apps/ALPHA", "app.alpha", "apps/Beta", "beta"],
            manifests: manifests);

        Assert.Equal(["alpha_ping", "beta_export"], tools.Select(static t => t.Name).ToArray());
    }

    [Fact]
    public async Task AgentTurnLoopExposesOpenAppOperationsAndDropsThemWhenAppCloses()
    {
        // Register isolated synthetic manifests for this test so it never depends on host app assemblies.
        ComponentManifests.Register(AppManifestOf("slice6-open-app", "Open Probe", "refresh"));
        ComponentManifests.Register(AppManifestOf("slice6-closed-app", "Closed Probe", "secret"));
        ComponentManifests.Register(AppManifestOf("slice6-always-app", "Always Probe", "status"));

        await using var os = TestOS.Local();
        os.Register<FakeWorkspace>();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));

        var workspace = os.Get<IWorkspace>(new NeuronId(Brain, IWorkspace.Type, "main"));
        await workspace.Show("apps/slice6-open-app", "op-open-1", 0);

        var chatClient = new RecordingChatClient();
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>();
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton<IChatClient>(chatClient);
        services.AddSingleton<INeuronProxies>(os.Proxies);
        using var provider = services.BuildServiceProvider();

        var loop = new AgentTurnLoop(provider);

        // Turn 1: slice6-open-app is open in IWorkspace; slice6-always-app is always-on; slice6-closed-app is closed.
        var plan1 = new TurnPlan(
            ScopeId: Brain.Value,
            Conversation: $"{Brain.Value}/conversations/main",
            RunId: "run-1",
            Transcript: [],
            Summary: null,
            Input: new AiMessage("user", [new AiText("Check status")]),
            Instructions: "Be concise.",
            Contracts: [],
            Contexts: [],
            Model: null,
            Options: null,
            MaxModelCalls: 4,
            Streaming: false)
        {
            IncludeComponentOperations = true,
            AlwaysOnOperationApps = ["slice6-always-app"],
        };

        await foreach (var _ in loop.Run(plan1, Ct)) { }

        var turn1Tools = Assert.Single(chatClient.SeenToolNamesPerCall);
        Assert.Contains("slice6_open_app_refresh", turn1Tools);
        Assert.Contains("slice6_always_app_status", turn1Tools);
        Assert.DoesNotContain("slice6_closed_app_secret", turn1Tools);

        // Close slice6-open-app in IWorkspace.
        var current = await workspace.Read();
        await workspace.Close("apps/slice6-open-app", "op-close-1", current.Revision);

        // Turn 2: closed app's operation disappears; always-on app remains.
        chatClient.SeenToolNamesPerCall.Clear();
        await foreach (var _ in loop.Run(plan1 with { RunId = "run-2" }, Ct)) { }

        var turn2Tools = Assert.Single(chatClient.SeenToolNamesPerCall);
        Assert.DoesNotContain("slice6_open_app_refresh", turn2Tools);
        Assert.Contains("slice6_always_app_status", turn2Tools);
        Assert.DoesNotContain("slice6_closed_app_secret", turn2Tools);
    }

    [Neuron(IWorkspace.Type)]
    private sealed class FakeWorkspace : Neuron, IWorkspace
    {
        private readonly List<WorkspaceViewRef> _openViews = [];
        private long _revision;

        public Task<WorkspaceState> Apply(WorkspaceUpdate next, string operationId, long expectedRevision)
        {
            _openViews.Clear();
            _openViews.AddRange(next.OpenViews);
            _revision++;
            return Read();
        }

        public Task<WorkspaceState> Show(string app, string operationId, long expectedRevision)
        {
            if (!_openViews.Any(v => string.Equals(v.App, app, StringComparison.OrdinalIgnoreCase)))
            {
                _openViews.Add(new WorkspaceViewRef(app, app, "full", string.Empty));
            }
            _revision++;
            return Read();
        }

        public Task<WorkspaceState> Back(string operationId, long expectedRevision) => Read();

        public Task<WorkspaceState> Home(string operationId, long expectedRevision)
        {
            _openViews.Clear();
            _revision++;
            return Read();
        }

        public Task<WorkspaceState> Close(string app, string operationId, long expectedRevision)
        {
            _openViews.RemoveAll(v => string.Equals(v.App, app, StringComparison.OrdinalIgnoreCase));
            _revision++;
            return Read();
        }

        public Task<WorkspaceState> Read()
        {
            var views = _openViews.ToArray();
            var open = views.Select(static v => v.App).ToArray();
            return Task.FromResult(new WorkspaceState(_revision, [], open) { OpenViews = views });
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<string[]> SeenToolNamesPerCall { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var names = (options?.Tools ?? [])
                .OfType<AIFunction>()
                .Select(static t => t.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            SeenToolNamesPerCall.Add(names);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
