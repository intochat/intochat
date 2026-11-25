using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using IntoChat.OS.Tests.Unit;

namespace IntoChat.OS.Modules.Flutter;

// A person's confirmation answer and the embedded browser's connector reports have their own routes; neither is a UI action,
// and POST /apps/event no longer carries either.
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

    // Only a direct person answers: a person speaking inside a conversation, an agent and an app are refused.
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

    // The old way is gone: a confirmation sent as a UI event is an unknown event, and nothing is resolved.
    [Fact]
    public async Task AConfirmationIsNoLongerAUiEvent()
    {
        var brain = new RecordingBrain();
        using var person = AsPerson();

        await Assert.ThrowsAsync<ArgumentException>(() => AppUiEndpoints.Deliver(
            new AppUiEndpoints.UiEvent(UIVocabulary.ConfirmationType, IConfirmationInbox.Key(Scope)), brain, Scope, null, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));

        Assert.Empty(brain.Inbox.Resolved);
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

    // The old way is gone: a browser report sent as a UI event is an unknown event.
    [Fact]
    public async Task ABrowserReportIsNoLongerAUiEvent()
    {
        var brain = new RecordingBrain();

        await Assert.ThrowsAsync<ArgumentException>(() => AppUiEndpoints.Deliver(
            new AppUiEndpoints.UiEvent(UIVocabulary.WebBrowserType, "owner/apps/researcher/browser", Value: """{"port":9222,"sessionId":"s"}"""),
            brain, Scope, null, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));

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
