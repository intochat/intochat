using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using IntoChat.OS.Tests.Unit;

namespace IntoChat.OS.Modules.Flutter;

// A person's confirmation answer and the embedded browser's connector reports have their own routes; neither is a UI action.
public sealed class ConfirmationAndBrowserRoutesFacts
{
    private const string Scope = "owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDisposable AsPerson(string? conversation = null) => IntoChat.OS.Tests.TestEdge.Authority.Enter(
        IntoChat.OS.Tests.TestEdge.Authority.Person("person-ann", "person-ann", new BrainId(Scope), conversation));

    [Fact]
    public void TheConfirmationRouteIsOnePostPerPlan()
    {
        var routes = RouteTable.Of(ConfirmationEndpoints.Map, services => services.Known<INeuronProxies>());

        Assert.Equal(["POST /brains/{brainId}/confirmations/{**planId}"], routes);
    }

    // The screens surface lists, reads and acts; it answers no confirmation. The one answer is the confirmations route above, and the
    // screen directory has no approve of its own.
    [Fact]
    public void TheScreensRoutesAnswerNoConfirmation()
    {
        var routes = RouteTable.Of(IntoChat.OS.Modules.Flutter.Screens.ScreensEndpoints.Map, services => services.Known<INeuronProxies>());

        Assert.Equal(
            ["GET /brains/{brainId}/screens/", "GET /brains/{brainId}/screens/{appId}", "POST /brains/{brainId}/screens/{appId}/act"],
            routes);
        Assert.DoesNotContain(
            typeof(IntoChat.OS.Modules.Flutter.Screens.IScreenDirectory).GetMethods(),
            method => method.Name.Contains("Approv", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBrowserRoutesAreConnectDisconnectAndUnavailable()
    {
        var routes = RouteTable.Of(WebBrowserEndpoints.Map, services => services.Known<INeuronProxies>());

        Assert.Equal(
            ["POST /brains/{brainId}/browsers/connect", "POST /brains/{brainId}/browsers/disconnect", "POST /brains/{brainId}/browsers/unavailable"],
            routes);
    }

    [Fact]
    public async Task APersonAnswersAPlanOfTheirBrainsInbox()
    {
        var brain = new RecordingBrain();
        using var person = AsPerson();

        await ConfirmationEndpoints.Answer("plan-3", new(true), brain, Scope, IntoChat.OS.Tests.TestEdge.Authority.Current, Ct);

        Assert.Equal(("owner/confirmations", "plan-3", true), Assert.Single(brain.Inbox.Resolved));
    }

    // The answer is typed: a missing answer, an empty plan or one longer than 128 characters is refused before anything is resolved.
    [Fact]
    public async Task AConfirmationAnswerIsTyped()
    {
        var brain = new RecordingBrain();
        using var person = AsPerson();
        var caller = IntoChat.OS.Tests.TestEdge.Authority.Current;

        await Assert.ThrowsAsync<ArgumentException>(() => ConfirmationEndpoints.Answer("plan-3", new(null), brain, Scope, caller, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConfirmationEndpoints.Answer("", new(true), brain, Scope, caller, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConfirmationEndpoints.Answer(new string('p', 129), new(true), brain, Scope, caller, Ct));

        Assert.Empty(brain.Inbox.Resolved);
    }

    // Only a direct person answers: a person speaking inside a conversation, an agent and an app are refused. Another brain's person
    // never reaches this brain's inbox: the route's membership filter refuses them (Gate20EdgeApprovalWallFacts, over real HTTP).
    [Fact]
    public async Task ANonDirectCallerCannotAnswer()
    {
        var brain = new RecordingBrain();
        var ann = new DigitalBrain.Access.Actor.Person("person-ann", "person-ann");
        DigitalBrain.Access.Caller[] callers =
        [
            IntoChat.OS.Tests.TestEdge.Authority.Person("person-ann", "person-ann", new BrainId(Scope), conversation: "c-1"),
            IntoChat.OS.Tests.TestEdge.Authority.Agent(ann, new BrainId(Scope)),
            IntoChat.OS.Tests.TestEdge.Authority.App("owner/apps/tasks", ann, new BrainId(Scope)),
        ];

        foreach (var caller in callers)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ConfirmationEndpoints.Answer("plan-3", new(true), brain, Scope, caller, Ct));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ConfirmationEndpoints.Answer("plan-3", new(true), brain, Scope, null, Ct));

        Assert.Empty(brain.Inbox.Resolved);
    }

    // The route's gate is the inbox's own Answerer rule, so the edge refuses up front exactly whom the inbox would not take as the person.
    [Fact]
    public async Task TheGateIsTheInboxAnswererRule()
    {
        await using var os = DigitalBrain.Testing.TestOS.Local();
        var brain = new BrainId(Scope);
        var ann = new DigitalBrain.Access.Actor.Person("ann", "acct");
        foreach (var caller in new[]
        {
            os.Edge.Person("ann", "acct", brain),
            os.Edge.Person("ann", "acct", brain, "chat-1"),
            os.Edge.Agent(ann, brain),
            os.Edge.App("app.settings", ann, brain),
            os.Edge.RelayedPerson("ann", "acct", brain),
            os.Edge.Scheduler(brain),
        })
        {
            var refused = Record.Exception(() => ShellPresenceGate.RequireDirectPerson(caller)) is UnauthorizedAccessException;
            Assert.Equal(DigitalBrain.Confirmation.Answerer.Of(caller, brain).IsPerson, !refused);
        }
    }

    [Fact]
    public async Task TheBrowserRoutesReachTheNamedBrowser()
    {
        var brain = new RecordingBrain();
        const string name = "owner/apps/researcher/browser";

        await WebBrowserEndpoints.Connect(new(name, 9222, "session-1"), brain, Scope, Ct);
        await WebBrowserEndpoints.Disconnect(new(name, "session-1"), brain, Scope, Ct);
        await WebBrowserEndpoints.Unavailable(new(name, "No WebView2 runtime."), brain, Scope, Ct);
        await WebBrowserEndpoints.Unavailable(new(name, null), brain, Scope, Ct);

        Assert.Equal(
            [
                (name, "connect 9222 session-1"),
                (name, "disconnect session-1"),
                (name, "unavailable No WebView2 runtime."),
                (name, "unavailable The browser is unavailable."),
            ],
            brain.Browser.Calls);
    }

    // A browser is named by its app UI in this brain; another brain's name, a name outside the app UI, and a missing session or
    // port are refused before the browser neuron is called.
    [Fact]
    public async Task ABrowserReportIsForThisBrainsAppUiAndTyped()
    {
        var brain = new RecordingBrain();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WebBrowserEndpoints.Connect(new("other/apps/researcher/browser", 9222, "s"), brain, Scope, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WebBrowserEndpoints.Connect(new("owner/identity/vault", 9222, "s"), brain, Scope, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WebBrowserEndpoints.Unavailable(new(null, "x"), brain, Scope, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => WebBrowserEndpoints.Connect(new("owner/apps/researcher/browser", null, "s"), brain, Scope, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => WebBrowserEndpoints.Connect(new("owner/apps/researcher/browser", 9222, null), brain, Scope, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => WebBrowserEndpoints.Disconnect(new("owner/apps/researcher/browser", ""), brain, Scope, Ct));

        Assert.Empty(brain.Browser.Calls);
    }

    private sealed class RecordingBrain : INeuronProxies
    {
        public RecordingInbox Inbox { get; } = new();
        public RecordingBrowser Browser { get; } = new();

        public T Get<T>(NeuronId id) where T : class
        {
            var key = id.Brain.Value + "/" + id.Key;
            if (typeof(T) == typeof(IConfirmationInbox)) { Inbox.Key = key; return (T)(object)Inbox; }
            if (typeof(T) == typeof(IWebBrowserConnector)) { Browser.Key = key; return (T)(object)Browser; }
            throw new InvalidOperationException($"No {typeof(T).Name} in this fake.");
        }
    }

    private sealed class RecordingInbox : IConfirmationInbox
    {
        public string Key { get; set; } = "";
        public List<(string Key, string PlanId, bool Approve)> Resolved { get; } = [];
        public Task<ConfirmationInbox> Read() => Task.FromResult(new ConfirmationInbox(0, []));
        public Task Resolve(string planId, bool approve) { Resolved.Add((Key, planId, approve)); return Task.CompletedTask; }
        public Task ResolveOption(string planId, int optionIndex) { Resolved.Add((Key, planId, true)); return Task.CompletedTask; }
    }

    private sealed class RecordingBrowser : IWebBrowserConnector
    {
        public string Key { get; set; } = "";
        public List<(string Key, string Call)> Calls { get; } = [];
        public Task Connect(int port, string sessionId) { Calls.Add((Key, $"connect {port} {sessionId}")); return Task.CompletedTask; }
        public Task Disconnect(string sessionId) { Calls.Add((Key, $"disconnect {sessionId}")); return Task.CompletedTask; }
        public Task Unavailable(string reason) { Calls.Add((Key, $"unavailable {reason}")); return Task.CompletedTask; }
    }
}
