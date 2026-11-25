using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Errors;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestOS = DigitalBrain.Testing.TestOS;
using Xunit;

namespace IntoChat.OS.Modules.Flutter;

public sealed class ScreenDirectoryNeuronFacts
{
    private const string Scope = "owner";

    private static IDisposable AsPerson(string principal = "alice")
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person(principal, principal, new BrainId(Scope)));

    private sealed class MockNeuronProxies : INeuronProxies
    {
        private readonly Dictionary<(Type Type, string Key), object> _byKey = new();

        public void Register<T>(string key, T instance) where T : class
        {
            _byKey[(typeof(T), key)] = instance;
        }

        public void Register<T>(NeuronId id, T instance) where T : class
        {
            _byKey[(typeof(T), $"{id.Brain.Value}/{id.Key}")] = instance;
            _byKey[(typeof(T), id.Key)] = instance;
        }

        public T Get<T>(NeuronId id) where T : class
        {
            if (_byKey.TryGetValue((typeof(T), $"{id.Brain.Value}/{id.Key}"), out var val))
            {
                return (T)val;
            }
            if (_byKey.TryGetValue((typeof(T), id.Key), out val))
            {
                return (T)val;
            }
            throw new KeyNotFoundException($"No mock neuron for {typeof(T).Name}:{id.Type}:{id.Key}");
        }
    }

    private sealed class MockWorkspace : IWorkspace
    {
        public WorkspaceState State { get; set; } = new(1L, ["owner"], [])
        {
            OpenViews = [
                new WorkspaceViewRef("v-calc", "owner/apps/calculator", "full", ""),
                new WorkspaceViewRef("v-unit", "owner/units/counter", "full", "")
            ],
            FocusedViewId = "v-calc"
        };

        public Task<WorkspaceState> Apply(WorkspaceUpdate next, string operationId, long expectedRevision) => Task.FromResult(State);
        public Task<WorkspaceState> Show(string app, string operationId, long expectedRevision) => Task.FromResult(State);
        public Task<WorkspaceState> Back(string operationId, long expectedRevision) => Task.FromResult(State);
        public Task<WorkspaceState> Home(string operationId, long expectedRevision) => Task.FromResult(State);
        public Task<WorkspaceState> Close(string app, string operationId, long expectedRevision) => Task.FromResult(State);
        public Task<WorkspaceState> Read() => Task.FromResult(State);
    }

    private sealed class MockApprovals : IApprovals
    {
        public List<Approval> Approvals { get; } =
        [
            new("plan-1", ApprovalKind.FlowStep, "calculator", "Approve Run", "Detail", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "trace-1")
        ];

        public Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Approval>>(Approvals);
    }

    private sealed class MockConfirmationInbox(params string[] pendingPlanIds) : IConfirmationInbox, IConfirmationRequests
    {
        private readonly HashSet<string> _pending = new(pendingPlanIds, StringComparer.Ordinal);
        private readonly Dictionary<string, ConfirmationResult> _outcomes = new(StringComparer.Ordinal);
        public List<(string PlanId, bool Approved)> Resolved { get; } = [];

        public Task<ConfirmationInbox> Read() => Task.FromResult(new ConfirmationInbox(
            0L,
            [.. _pending.Select(planId => new ConfirmationRequested(
                planId,
                new ConfirmationPlan(planId, ConfirmationAction.Other, false, planId, planId, "Approve", "Deny", planId),
                ConfirmationPrincipal.App("creator"),
                default,
                DateTimeOffset.UtcNow.AddHours(1)))]));

        public Task Resolve(string planId, bool approved)
        {
            Resolved.Add((planId, approved));
            _pending.Remove(planId);
            _outcomes[planId] = new ConfirmationResult(
                planId,
                approved ? ConfirmationOutcome.Approved : ConfirmationOutcome.Denied,
                approved ? ConfirmationDenyReason.None : ConfirmationDenyReason.ByPerson,
                Skipped: false);
            return Task.CompletedTask;
        }

        public Task<ConfirmationResult?> Request(ConfirmationPlan plan) => Task.FromResult<ConfirmationResult?>(null);

        public Task<ConfirmationResult?> Outcome(string planId)
            => Task.FromResult(_outcomes.TryGetValue(planId, out var outcome) ? outcome : null);

        public Task Withdraw(string planId)
        {
            _pending.Remove(planId);
            return Task.CompletedTask;
        }
    }

    // A sandboxed app's view neuron: it stores the view and takes the app's commands through IScreen.Dispatch.
    private sealed class MockView : IView, IScreen
    {
        public PublishedView? Published { get; set; }
        public List<Command> Dispatched { get; } = [];

        public Task Publish(long generation, View view) => Task.CompletedTask;

        public Task<PublishedView?> Read() => Task.FromResult(Published);

        public Task<PublishedView?> Open() => Read();

        public Task<PublishedView?> Read(string? alias) => Read();

        public Task<PublishedView?> ReadChild(string path) => Task.FromException<PublishedView?>(new ActivationRefusedException(ActivationRefusedException.UnknownChild));

        public Task Request(string signal, JsonElement? args = null) => Dispatch(new Command(".", signal, args));

        public Task Dispatch(Command command)
        {
            Dispatched.Add(command);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ScreenDirectory_List_MergesWorkspaceAndApprovals()
    {
        using var person = AsPerson();
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);

        var proxies = new MockNeuronProxies();
        var ws = new MockWorkspace();
        var approvals = new MockApprovals();
        proxies.Register<IWorkspace>(IWorkspace.Key(Scope), ws);
        proxies.Register<IApprovals>(IApprovals.Key(Scope), approvals);

        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(proxies)
            .BuildServiceProvider();
        await using var osWithProxies = TestOS.Local(o => o.Services = services).Register(typeof(FlutterModule).Assembly);

        var neuron = osWithProxies.Get<IScreenDirectory>(new NeuronId(new BrainId(Scope), IScreenDirectory.TypeName, IScreenDirectory.Key(Scope)));

        var list = await neuron.List();

        Assert.NotEmpty(list);
        var calc = Assert.Single(list, s => s.AppId == "calculator");
        Assert.True(calc.OpenInWorkspace);
        Assert.True(calc.Focused);
        Assert.Equal(1, calc.PendingApprovals);

        var counter = Assert.Single(list, s => s.AppId == "counter");
        Assert.True(counter.OpenInWorkspace);
        Assert.False(counter.Focused);

        var ino = Assert.Single(list, s => s.AppId == "ino");
        Assert.False(ino.OpenInWorkspace);
        Assert.False(ino.Focused);
    }

    [Fact]
    public async Task ScreenDirectory_Read_ExtractsControlsAcrossTree()
    {
        using var person = AsPerson();
        var proxies = new MockNeuronProxies();
        var view = new View("Calculator", new Column(
        [
            new Text("Title"),
            new Button("btn-add", "Add", new Command(".", "add", null)),
            new Field("fld-exp", "owner/apps/calc/exp", FieldKind.Text, Placeholder: "Type exp"),
            new ApprovalCardNode("card-1", "plan-99", "Approve Plan", "Summary", [], Resolve: new Resolve("plan-99")),
            new NavItemNode("nav-1", "Navigation", "icon", "nav-route", Command: new Command(".", "nav", null))
        ]));
        var json = JsonSerializer.SerializeToElement(view, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));

        var mockView = new MockView { Published = new PublishedView("owner/apps/calculator/view", 2L, json) };
        proxies.Register<IView>("owner/apps/calculator/view", mockView);
        proxies.Register<IScreen>("owner/apps/calculator/view", mockView);
        proxies.Register<IWorkspace>(IWorkspace.Key(Scope), new MockWorkspace());
        proxies.Register<IApprovals>(IApprovals.Key(Scope), new MockApprovals());

        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(proxies)
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services).Register(typeof(FlutterModule).Assembly);
        var neuron = os.Get<IScreenDirectory>(new NeuronId(new BrainId(Scope), IScreenDirectory.TypeName, IScreenDirectory.Key(Scope)));
        var snapshot = await neuron.Read("calculator");

        Assert.NotNull(snapshot);
        Assert.Equal("calculator", snapshot.AppId);
        Assert.Equal(2L, snapshot.Generation);

        // Verify control extraction
        Assert.Contains(snapshot.Controls, c => c.Key == "btn-add" && c.Kind == "button" && c.Label == "Add");
        Assert.Contains(snapshot.Controls, c => c.Key == "fld-exp" && c.Kind == "field");
        Assert.Contains(snapshot.Controls, c => c.Key == "card-1" && c.Kind == "approvalCard");
        Assert.Contains(snapshot.Controls, c => c.Key == "nav-1" && c.Kind == "navItem");

        // Verify approval extraction from resolve node
        Assert.Contains(snapshot.PendingApprovals, a => a.ApprovalId == "plan-99");
    }

    [Fact]
    public async Task ScreenDirectory_Act_InvokesActivationAndReturnsUpdatedSnapshot()
    {
        using var person = AsPerson();
        var proxies = new MockNeuronProxies();
        var view = new View("Calculator", new Column([new Button("btn-1", "One", new Command(".", "1", null))]));
        var json = JsonSerializer.SerializeToElement(view, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));

        var mockView = new MockView { Published = new PublishedView("owner/apps/calculator/view", 1L, json) };
        proxies.Register<IView>("owner/apps/calculator/view", mockView);
        proxies.Register<IScreen>("owner/apps/calculator/view", mockView);
        proxies.Register<IWorkspace>(IWorkspace.Key(Scope), new MockWorkspace());
        proxies.Register<IApprovals>(IApprovals.Key(Scope), new MockApprovals());

        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(proxies)
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services).Register(typeof(FlutterModule).Assembly);
        var neuron = os.Get<IScreenDirectory>(new NeuronId(new BrainId(Scope), IScreenDirectory.TypeName, IScreenDirectory.Key(Scope)));

        // Act with generation = null should pick current generation 1L
        var result = await neuron.Act("calculator", "btn-1", "value", generation: null);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Screen);
        // The node's command goes to the view neuron's Dispatch with the value joined to its args.
        var sent = Assert.Single(mockView.Dispatched);
        Assert.Equal((".", "1"), (sent.Target, sent.Signal));
        Assert.Equal("value", sent.Args?.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ScreenDirectory_Act_RefusesResolveOnAppView()
    {
        using var person = AsPerson();
        var proxies = new MockNeuronProxies();
        var view = new View("Calculator", new Column([new ApprovalCardNode("card-1", "plan-99", "Approve", "Summary", [], Resolve: new Resolve("plan-99"))]));
        var json = JsonSerializer.SerializeToElement(view, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));

        var mockView = new MockView { Published = new PublishedView("owner/apps/calculator/view", 1L, json) };
        var inbox = new MockConfirmationInbox();
        proxies.Register<IView>("owner/apps/calculator/view", mockView);
        proxies.Register<IScreen>("owner/apps/calculator/view", mockView);
        proxies.Register<IConfirmationInbox>(IConfirmationInbox.Key(Scope), inbox);
        proxies.Register<IWorkspace>(IWorkspace.Key(Scope), new MockWorkspace());
        proxies.Register<IApprovals>(IApprovals.Key(Scope), new MockApprovals());

        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(proxies)
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services).Register(typeof(FlutterModule).Assembly);
        var neuron = os.Get<IScreenDirectory>(new NeuronId(new BrainId(Scope), IScreenDirectory.TypeName, IScreenDirectory.Key(Scope)));
        var result = await neuron.Act("calculator", "card-1", "approve", generation: 1L);

        Assert.False(result.Accepted);
        Assert.Empty(inbox.Resolved);
        Assert.Empty(mockView.Dispatched);
    }

    [Fact]
    public async Task ScreenDirectory_Approve_ResolvesApproval_AndRefusesDoubleResolution()
    {
        using var person = AsPerson();
        var proxies = new MockNeuronProxies();
        var inbox = new MockConfirmationInbox("plan-42");
        proxies.Register<IConfirmationInbox>(IConfirmationInbox.Key(Scope), inbox);
        proxies.Register<IConfirmationRequests>(IConfirmationRequests.Of(new BrainId(Scope)), inbox);

        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(proxies)
            .BuildServiceProvider();
        await using var osWithProxies = TestOS.Local(o => o.Services = services).Register(typeof(FlutterModule).Assembly);

        var neuron = osWithProxies.Get<IScreenDirectory>(new NeuronId(new BrainId(Scope), IScreenDirectory.TypeName, IScreenDirectory.Key(Scope)));

        // First approval: accepted
        var first = await neuron.Approve("plan-42", approved: true);
        Assert.True(first.Accepted);
        Assert.Contains("approved", first.Message);

        // Second approval for same planId: refused
        var second = await neuron.Approve("plan-42", approved: true);
        Assert.False(second.Accepted);
        Assert.Contains("already been resolved", second.Message);
    }

    [Fact]
    public async Task ScreenDirectory_List_LogsSwallowedWorkspaceFailureToInjectedLogger()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var proxies = new MockNeuronProxies();
        var logger = new RecordingLogger<ScreenDirectoryNeuron>();
        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(proxies)
            .AddSingleton<ILogger<ScreenDirectoryNeuron>>(logger)
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services).Register(typeof(FlutterModule).Assembly);
        var neuron = os.Get<IScreenDirectory>(new NeuronId(new BrainId(Scope), IScreenDirectory.TypeName, IScreenDirectory.Key(Scope)));

        await neuron.List();

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Workspace could not be read for screen directory.", StringComparison.Ordinal));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
