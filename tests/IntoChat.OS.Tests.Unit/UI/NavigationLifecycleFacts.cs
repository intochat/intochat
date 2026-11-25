using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using IntoChat.OS.UI.Dispatch;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Tests.Unit.UI;

[Signal("nav.sample.arrived")]
public sealed record NavSampleArrived(string Section, string? TraceId) : Signal;

public sealed record NavSampleState(
    string Section = "resources",
    string? TraceId = null,
    bool HasUnsavedDraft = false,
    string? LastDepartedTo = null)
{
    public NavSampleState() : this("resources", null, false, null) { }

    [Invariant("Section is never blank.")]
    public bool HasValidSection => !string.IsNullOrWhiteSpace(Section);

    public Rule CleanBeforeDeparture => Decision.Require(
        "unsaved_draft",
        this,
        static s => !s.HasUnsavedDraft,
        "Save or discard draft changes before leaving this screen.");

    public Decision<NavSampleState> OpenRoute(
        [Sample("traces")] string section,
        [Sample("trace-42")] string? traceId = null)
    {
        var normalized = string.IsNullOrWhiteSpace(section) ? "resources" : section.Trim();
        return Decision.Accept(
            this with { Section = normalized, TraceId = traceId },
            new NavSampleArrived(normalized, traceId));
    }

    public NavSampleState MarkDirty(bool dirty) => this with { HasUnsavedDraft = dirty };

    public NavSampleState RecordDeparture(string? target) => this with { LastDepartedTo = target };
}

[Neuron("app.test.nav-sample")]
[Screen("app.test.nav-sample", Title = "Navigation Sample", Icon = Icons.Sliders)]
public sealed class NavSampleScreen : AggregateScreen<NavSampleState>, INavigatedTo<BrowserNavArgs>, INavigatedFrom
{
    public async Task OnNavigatedTo(NavigationArrival<BrowserNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        var section = arrival.Args.Section ?? "resources";
        await ApplyDecisionAsync(State.OpenRoute(section, arrival.Args.TraceId)).ConfigureAwait(true);
    }

    public Rule CanNavigateFrom(NavigationDeparture departure) => State.CleanBeforeDeparture;

    public async Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        await Store(State.RecordDeparture(departure.ToNeuron ?? departure.Route ?? "departed")).ConfigureAwait(true);
    }

    protected override Node Render(NavSampleState state) =>
        AppShell(
            key: "nav-sample-shell",
            sidebar: VStack(Space.Xs)[
                NavItem("nav-resources", "Resources", Icons.LayoutGrid, "resources", selected: state.Section == "resources"),
                NavItem("nav-traces", "Traces", Icons.Sparkles, "traces", selected: state.Section == "traces")
            ],
            toolbar: Toolbar(
                Heading($"Section: {state.Section}", level: 4),
                Button("Deep-Link Trace")
                    .Key("btn-nav-trace")
                    .Sends(NavigateToCommand(
                        new BrowserNavArgs(Section: "traces", TraceId: "span-99"),
                        fromNeuron: "app.engineer",
                        transition: ViewTransition.SharedAxisX)),
                Button("Leave Screen")
                    .Key("btn-leave")
                    .Sends(NavigateFromCommand(toNeuron: "app.creator", route: "brain")),
                Button("Force Leave")
                    .Key("btn-force-leave")
                    .Sends(NavigateFromCommand(toNeuron: "app.creator", route: "brain", force: true)),
                Button("Toggle Dirty")
                    .Key("btn-toggle-dirty")
                    .OnClick<NavSampleState>(x => x.MarkDirty(!state.HasUnsavedDraft))),
            body: WebBrowser(
                url: state.TraceId is { Length: > 0 } tid
                    ? $"https://dashboard.example.test/{state.Section}/detail/{tid}"
                    : $"https://dashboard.example.test/{state.Section}",
                title: "Operator Surface",
                key: "nav-sample-browser",
                height: 820),
            inspector: Card("Inspector", state.LastDepartedTo ?? "Active")[
                LastRejection is { } rej
                    ? Banner("Navigation blocked", rej.Messages[0], tone: "warning")
                    : Text($"Trace: {state.TraceId ?? "none"}")
            ]);
}

public sealed class NavigationLifecycleFacts
{
    [Fact]
    public void ViewTransitionValidatesAndNormalizesKnownModes()
    {
        Assert.True(ViewTransition.IsValid(""));
        Assert.True(ViewTransition.IsValid(ViewTransition.None));
        Assert.True(ViewTransition.IsValid(ViewTransition.FadeThrough));
        Assert.True(ViewTransition.IsValid(ViewTransition.SharedAxisX));
        Assert.True(ViewTransition.IsValid(ViewTransition.SharedAxisZ));
        Assert.True(ViewTransition.IsValid(ViewTransition.CameraFlyTo));
        Assert.False(ViewTransition.IsValid("teleport-3000"));

        Assert.Equal(ViewTransition.None, ViewTransition.Normalize(null));
        Assert.Equal(ViewTransition.None, ViewTransition.Normalize("   "));
        Assert.Equal(ViewTransition.SharedAxisZ, ViewTransition.Normalize(" shared-axis-z "));
        Assert.Equal(ViewTransition.FadeThrough, ViewTransition.Normalize("unknown-mode"));
    }

    [Fact]
    public void BuiltInSignalAliasesExposeCommandAndNavigationSignals()
    {
        var aliases = AggregateScreenBase<NavSampleState>.BuiltInSignalAliases;
        Assert.Contains(UiCommand.SignalAlias, aliases);
        Assert.Contains(UiNavigateTo.SignalAlias, aliases);
        Assert.Contains(UiNavigateFrom.SignalAlias, aliases);
    }

    [Fact]
    public void NavSampleScreenPassesAggregateExplorerWithZeroViolations()
    {
        var graph = AggregateExplorer<NavSampleState>.ExploreScreen<NavSampleScreen>(maxStates: 16);
        graph.EnsureHealthy();
        Assert.True(graph.IsHealthy);
        Assert.Empty(graph.Violations);
    }

    [Fact]
    public async Task LiveScreenHandlesUiNavigateToAndEnforcesINavigatedFromRuleGuards()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register<NavSampleScreen>();

        var rejectedHeard = new List<Rejected>();
        await using var sub = await os.ClusterListeners.Listen<Rejected>(
            "nav-rejected-listener",
            (_, signal, _) =>
            {
                rejectedHeard.Add(signal);
                return Task.CompletedTask;
            });

        var brain = new BrainId("test");
        var id = new NeuronId(brain, "app.test.nav-sample", "default");
        var screen = os.Get<IScreen>(id);

        var initial = await screen.Open();
        Assert.NotNull(initial);
        Assert.Contains("Section: resources", initial!.View.GetRawText(), StringComparison.Ordinal);

        // 1. Trigger typed arrival via UiCommand button helper (NavigateToCommand -> INavigatedTo<BrowserNavArgs>)
        await screen.Activate("app.test.nav-sample", initial.Generation, "btn-nav-trace");
        await os.Idle();

        var afterNav = await screen.Open();
        Assert.NotNull(afterNav);
        var afterNavJson = afterNav!.View.GetRawText();
        Assert.Contains("Section: traces", afterNavJson, StringComparison.Ordinal);
        Assert.Contains("traces/detail/span-99", afterNavJson, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"shared-axis-x\"", afterNavJson, StringComparison.Ordinal);

        // 2. Mark draft dirty, then attempt unforced departure -> refused by CleanBeforeDeparture rule!
        await screen.Activate("app.test.nav-sample", afterNav.Generation, "btn-toggle-dirty");
        await os.Idle();
        var dirtyView = await screen.Open();
        Assert.NotNull(dirtyView);

        await screen.Activate("app.test.nav-sample", dirtyView!.Generation, "btn-leave");
        await os.Idle();

        Assert.Contains(rejectedHeard, r => r.Rules.Contains("unsaved_draft"));
        var blockedView = await screen.Open();
        Assert.NotNull(blockedView);
        Assert.Contains("Save or discard draft changes before leaving this screen.", blockedView!.View.GetRawText(), StringComparison.Ordinal);

        // 3. Forced departure bypasses soft refusal guard, clears LastRejection, and runs OnNavigatedFrom!
        await screen.Activate("app.test.nav-sample", blockedView.Generation, "btn-force-leave");
        await os.Idle();

        var departedView = await screen.Open();
        Assert.NotNull(departedView);
        Assert.Contains("app.creator", departedView!.View.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Navigation blocked", departedView.View.GetRawText(), StringComparison.Ordinal);

        // 4. Direct UiNavigateTo signal via IScreen.Request (including camelCase Web JSON) updates state and stamps transition!
        var directPayload = "{\"section\":\"metrics\",\"traceId\":\"camel-trace-7\"}";
        var signalJson = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
            new UiNavigateTo(directPayload, FromNeuron: "app.orleans", Transition: ViewTransition.SharedAxisZ),
            typeof(UiNavigateTo));
        await screen.Request(UiNavigateTo.SignalAlias, signalJson);
        await os.Idle();

        var afterDirect = await screen.Open();
        Assert.NotNull(afterDirect);
        Assert.Contains("Section: metrics", afterDirect!.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("metrics/detail/camel-trace-7", afterDirect.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"shared-axis-z\"", afterDirect.View.GetRawText(), StringComparison.Ordinal);

        var studioJson = JsonSerializer.Serialize(new StudioNavArgs(Lens: "plan", CandidateId: "w2"), JsonSerializerOptions.Web);
        var table = NavigationDispatchTable.Build(typeof(StudioProbeScreen));
        var probe = new StudioProbeScreen();
        await table.InvokeJsonAsync(probe, studioJson, "app.creator", ViewTransition.SharedAxisZ, null, TestContext.Current.CancellationToken);
        Assert.Equal("w2", probe.Received?.Args.CandidateId);
        Assert.Equal("plan", probe.Received?.Args.Lens);
    }

    private sealed class StudioProbeScreen : AggregateScreen<NavSampleState>, INavigatedTo<StudioNavArgs>
    {
        public NavigationArrival<StudioNavArgs>? Received { get; private set; }

        public Task OnNavigatedTo(NavigationArrival<StudioNavArgs> arrival, CancellationToken cancellationToken = default)
        {
            Received = arrival;
            return Task.CompletedTask;
        }

        protected override Node Render(NavSampleState state) => Text("Probe");
    }

    [Fact]
    public async Task TypedTryNavigateFromAsyncEvaluatesRuleOfflineAndClearsOnForce()
    {
        var ct = TestContext.Current.CancellationToken;
        var screen = new UnattachedDirtyNavScreen();
        var allowed = await screen.TryNavigateFromAsync(new NavigationDeparture("app.aspire", "traces", Force: false), ct);
        Assert.False(allowed);
        Assert.NotNull(screen.LastRejection);
        Assert.Contains("unsaved_draft", screen.LastRejection!.Rules);
        Assert.False(screen.Departed);

        var forced = await screen.TryNavigateFromAsync(new NavigationDeparture("app.aspire", "traces", Force: true), ct);
        Assert.True(forced);
        Assert.Null(screen.LastRejection);
        Assert.True(screen.Departed);
    }

    private sealed class UnattachedDirtyNavScreen : AggregateScreen<NavSampleState>, INavigatedFrom
    {
        public bool Departed { get; private set; }

        public Rule CanNavigateFrom(NavigationDeparture departure)
            => Decision.Rule("unsaved_draft", departure, static _ => true, "Unsaved draft present.");

        public Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
        {
            Departed = true;
            return Task.CompletedTask;
        }

        protected override Node Render(NavSampleState state) => Text("Dirty");
    }

    private sealed class TasksProbeScreen : AggregateScreen<NavSampleState>, INavigatedTo<TasksNavArgs>
    {
        public NavigationArrival<TasksNavArgs>? Received { get; private set; }

        public Task OnNavigatedTo(NavigationArrival<TasksNavArgs> arrival, CancellationToken cancellationToken = default)
        {
            Received = arrival;
            return Task.CompletedTask;
        }

        protected override Node Render(NavSampleState state) => Text("TasksProbe");
    }

    private sealed class InoProbeScreen : AggregateScreen<NavSampleState>, INavigatedTo<InoNavArgs>
    {
        public NavigationArrival<InoNavArgs>? Received { get; private set; }

        public Task OnNavigatedTo(NavigationArrival<InoNavArgs> arrival, CancellationToken cancellationToken = default)
        {
            Received = arrival;
            return Task.CompletedTask;
        }

        protected override Node Render(NavSampleState state) => Text("InoProbe");
    }

    [Fact]
    public void AppSignalsOpenApp_TypedOverloadEncodesArgsAndTransition_WhilePlainOverloadStaysCompatible()
    {
        var plain = AppSignals.OpenApp("aspire", "4bf92f3577b34da6a3ce929d0e0e4736");
        Assert.Equal(IntentKind.OpenWindow, plain.Kind);
        Assert.Equal("os://window/open?app=aspire&input=4bf92f3577b34da6a3ce929d0e0e4736", plain.Arg);

        var noInput = AppSignals.OpenApp("orleans");
        Assert.Equal("os://window/open?app=orleans", noInput.Arg);

        var typed = AppSignals.OpenApp(
            "aspire",
            new BrowserNavArgs(TraceId: "4bf92f3577b34da6a3ce929d0e0e4736"),
            transition: ViewTransition.SharedAxisZ,
            fromNeuron: "app.engineer");
        Assert.Equal(IntentKind.OpenWindow, typed.Kind);

        var uri = new Uri(typed.Arg!);
        Assert.Equal("os", uri.Scheme);
        Assert.Equal("window", uri.Host);
        Assert.Equal("/open", uri.AbsolutePath);

        var query = ParseQuery(uri.Query);
        Assert.Equal("aspire", query["app"]);
        Assert.Equal("shared-axis-z", query["transition"]);
        Assert.Equal("app.engineer", query["from"]);
        Assert.Contains("4bf92f3577b34da6a3ce929d0e0e4736", query["args"], StringComparison.Ordinal);
        Assert.DoesNotContain("null", query["args"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainInputAndOpenWindowQueryParameters_CoerceTasks_Ino_Studio_AndEngineerApp_AndEnforceDepartureGuards()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. TasksNavArgs coercion from plain Input & structured JSON query parameters
        var tasksTable = NavigationDispatchTable.Build(typeof(TasksProbeScreen));
        var tasksProbe = new TasksProbeScreen();

        var tasksPlainIntent = AppSignals.OpenApp("tasks", "flow-build-77");
        var tasksPlainQuery = ParseQuery(new Uri(tasksPlainIntent.Arg!).Query);
        await tasksTable.InvokeJsonAsync(
            tasksProbe,
            argsJson: null,
            fromNeuron: "app.creator",
            transition: ViewTransition.FadeThrough,
            correlationId: null,
            cancellationToken: ct,
            input: tasksPlainQuery["input"]);
        Assert.Equal("flow-build-77", tasksProbe.Received?.Args.FlowId);
        Assert.Null(tasksProbe.Received?.Args.Tab);

        await tasksTable.InvokeJsonAsync(tasksProbe, "\"confirm:plan-19\"", "app.ino", ViewTransition.SharedAxisX, null, ct);
        Assert.Equal("approvals", tasksProbe.Received?.Args.Tab);
        Assert.Equal("plan-19", tasksProbe.Received?.Args.ConfirmationId);

        await tasksTable.InvokeJsonAsync(tasksProbe, "draft:issue-draft-3", "app.ino", ViewTransition.FadeThrough, null, ct);
        Assert.Equal("approvals", tasksProbe.Received?.Args.Tab);
        Assert.Equal("issue-draft-3", tasksProbe.Received?.Args.DraftPreviewId);

        var tasksTypedIntent = AppSignals.OpenApp(
            "tasks",
            new TasksNavArgs(FlowId: "flow-99", Tab: "table", ConfirmationId: "c-1", DraftPreviewId: "d-2"),
            transition: ViewTransition.SharedAxisZ,
            fromNeuron: "app.engineer");
        var tasksTypedQuery = ParseQuery(new Uri(tasksTypedIntent.Arg!).Query);
        await tasksTable.InvokeJsonAsync(
            tasksProbe,
            argsJson: tasksTypedQuery["args"],
            fromNeuron: tasksTypedQuery["from"],
            transition: tasksTypedQuery["transition"],
            correlationId: null,
            cancellationToken: ct);
        Assert.Equal("flow-99", tasksProbe.Received?.Args.FlowId);
        Assert.Equal("table", tasksProbe.Received?.Args.Tab);
        Assert.Equal("c-1", tasksProbe.Received?.Args.ConfirmationId);
        Assert.Equal("d-2", tasksProbe.Received?.Args.DraftPreviewId);

        // 2. InoNavArgs coercion from plain Input & typed OpenApp<InoNavArgs>
        var inoTable = NavigationDispatchTable.Build(typeof(InoProbeScreen));
        var inoProbe = new InoProbeScreen();
        await inoTable.InvokeJsonAsync(inoProbe, argsJson: null, fromNeuron: "app.tasks", transition: ViewTransition.FadeThrough, correlationId: null, cancellationToken: ct, input: "Triage GitHub issue #42");
        Assert.Equal("Triage GitHub issue #42", inoProbe.Received?.Args.Prompt);

        var inoTypedIntent = AppSignals.OpenApp(
            "ino",
            new InoNavArgs(Prompt: "Inspect trace", ConversationId: "conv-8", FocusTarget: "node:app.aspire"),
            transition: ViewTransition.FadeThrough,
            fromNeuron: "app.creator");
        var inoTypedQuery = ParseQuery(new Uri(inoTypedIntent.Arg!).Query);
        await inoTable.InvokeJsonAsync(inoProbe, inoTypedQuery["args"], inoTypedQuery["from"], inoTypedQuery["transition"], null, ct);
        Assert.Equal("conv-8", inoProbe.Received?.Args.ConversationId);
        Assert.Equal("node:app.aspire", inoProbe.Received?.Args.FocusTarget);

        // 3. Live EngineerApp deep-link via os://window/open query parameters & plain input + Dispatch departure guard
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(EngineerApp).Assembly);
        os.Register<NavSampleScreen>();

        var brain = new BrainId("test");
        var engineerScreen = os.Get<IScreen>(new NeuronId(brain, "app.engineer", "default"));
        _ = await engineerScreen.Open();

        var engineerTypedIntent = AppSignals.OpenApp(
            "engineer",
            new StudioNavArgs(Lens: "plan", CandidateId: "w2"),
            transition: ViewTransition.SharedAxisX,
            fromNeuron: "app.tasks");
        var engQuery = ParseQuery(new Uri(engineerTypedIntent.Arg!).Query);
        await engineerScreen.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    ArgsJson: engQuery["args"],
                    FromNeuron: engQuery["from"],
                    Transition: engQuery["transition"]),
                typeof(UiNavigateTo))));
        await os.Idle();

        var engViewJson = (await engineerScreen.Open())!.View.GetRawText();
        Assert.Contains("Winning candidate: w2", engViewJson, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"shared-axis-x\"", engViewJson, StringComparison.Ordinal);

        var engineerPlainIntent = AppSignals.OpenApp("engineer", "w1");
        var engPlainQuery = ParseQuery(new Uri(engineerPlainIntent.Arg!).Query);
        await engineerScreen.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    FromNeuron: "app.creator",
                    Transition: ViewTransition.FadeThrough,
                    Input: engPlainQuery["input"]),
                typeof(UiNavigateTo))));
        await os.Idle();
        Assert.Contains("Winning candidate: w1", (await engineerScreen.Open())!.View.GetRawText(), StringComparison.Ordinal);

        // 4. IScreen.Dispatch("ui.navigate-from") surfaces ActivationRefusedException with Rule:<code >: <message> when blocked
        var guarded = os.Get<IScreen>(new NeuronId(brain, "app.test.nav-sample", "guard-check"));
        var guardedInitial = await guarded.Open();
        await guarded.Activate("app.test.nav-sample", guardedInitial!.Generation, "btn-toggle-dirty");
        await os.Idle();

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            guarded.Dispatch(new Command(
                ".",
                UiNavigateFrom.SignalAlias,
                RuntimeProtocol.SerializeToElement(
                    new UiNavigateFrom(ToNeuron: "test/apps/aspire", Force: false),
                    typeof(UiNavigateFrom)))));
        Assert.StartsWith("Rule:unsaved_draft:", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Save or discard draft changes", refused.Reason, StringComparison.Ordinal);

        await guarded.Dispatch(new Command(
            ".",
            UiNavigateFrom.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "test/apps/aspire", Force: true),
                typeof(UiNavigateFrom))));
        await os.Idle();
        Assert.Contains("test/apps/aspire", (await guarded.Open())!.View.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class SettingsProbeScreen : AggregateScreen<NavSampleState>, INavigatedTo<SettingsNavArgs>
    {
        public NavigationArrival<SettingsNavArgs>? Received { get; private set; }

        public Task OnNavigatedTo(NavigationArrival<SettingsNavArgs> arrival, CancellationToken cancellationToken = default)
        {
            Received = arrival;
            return Task.CompletedTask;
        }

        protected override Node Render(NavSampleState state) => Text("SettingsProbe");
    }

    [Fact]
    public async Task SettingsNavArgs_CoercesPlainSectionAndSubPage_AndEngineerRendersConfigureGitHubAndAskInoDeepLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        var settingsTable = NavigationDispatchTable.Build(typeof(SettingsProbeScreen));
        var probe = new SettingsProbeScreen();

        // 1. Plain input "connections/github" -> Section: "connections", SubPage: "github"
        var ghIntent = AppSignals.OpenApp("settings", "connections/github");
        var ghQuery = ParseQuery(new Uri(ghIntent.Arg!).Query);
        Assert.Equal("settings", ghQuery["app"]);
        Assert.Equal("connections/github", ghQuery["input"]);

        await settingsTable.InvokeJsonAsync(
            probe,
            argsJson: null,
            fromNeuron: "app.engineer",
            transition: ViewTransition.SharedAxisX,
            correlationId: null,
            cancellationToken: ct,
            input: ghQuery["input"]);
        Assert.Equal("connections", probe.Received?.Args.Section);
        Assert.Equal("github", probe.Received?.Args.SubPage);
        Assert.Null(probe.Received?.Args.Sheet);
        Assert.Null(probe.Received?.Args.SearchQuery);

        // 2. Plain input "models/local-server" and single-segment "keys" / "billing"
        await settingsTable.InvokeJsonAsync(probe, "\"models/local-server\"", "app.ino", ViewTransition.FadeThrough, null, ct);
        Assert.Equal("models", probe.Received?.Args.Section);
        Assert.Equal("local-server", probe.Received?.Args.SubPage);

        await settingsTable.InvokeJsonAsync(probe, argsJson: "{}", fromNeuron: "app.desktop", transition: ViewTransition.FadeThrough, correlationId: null, cancellationToken: ct, input: "keys");
        Assert.Equal("keys", probe.Received?.Args.Section);
        Assert.Null(probe.Received?.Args.SubPage);

        await settingsTable.InvokeJsonAsync(probe, argsJson: null, fromNeuron: "app.ino", transition: ViewTransition.FadeThrough, correlationId: null, cancellationToken: ct, input: "billing");
        Assert.Equal("billing", probe.Received?.Args.Section);
        Assert.Null(probe.Received?.Args.SubPage);

        // 3. Typed OpenApp<SettingsNavArgs> round-trip
        var typedSettingsIntent = AppSignals.OpenApp(
            "settings",
            new SettingsNavArgs(Section: "connections", SubPage: "github", Sheet: "pat", SearchQuery: "webhook"),
            transition: ViewTransition.SharedAxisZ,
            fromNeuron: "app.engineer");
        var typedSettingsQuery = ParseQuery(new Uri(typedSettingsIntent.Arg!).Query);
        await settingsTable.InvokeJsonAsync(
            probe,
            argsJson: typedSettingsQuery["args"],
            fromNeuron: typedSettingsQuery["from"],
            transition: typedSettingsQuery["transition"],
            correlationId: null,
            cancellationToken: ct);
        Assert.Equal("connections", probe.Received?.Args.Section);
        Assert.Equal("github", probe.Received?.Args.SubPage);
        Assert.Equal("pat", probe.Received?.Args.Sheet);
        Assert.Equal("webhook", probe.Received?.Args.SearchQuery);

        // 4. EngineerViewRenderer renders compact Configure GitHub ↗ and Ask Ino ↗ deep-links in PR Review header area
        var engineerRoot = EngineerViewRenderer.RenderRoot(new EngineerWorkspaceState(), lastRejection: null);
        var engineerWire = JsonSerializer.Serialize(engineerRoot, RuntimeProtocol.OptionsFor(typeof(Node)));
        Assert.Contains("engineer:open-github-settings", engineerWire, StringComparison.Ordinal);
        Assert.Contains("Configure GitHub", engineerWire, StringComparison.Ordinal);
        Assert.Contains("os://window/open?app=settings", engineerWire, StringComparison.Ordinal);
        Assert.Contains("connections%2Fgithub", engineerWire, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("engineer:ask-ino", engineerWire, StringComparison.Ordinal);
        Assert.Contains("Ask Ino", engineerWire, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string rawQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return result;
    }
}
