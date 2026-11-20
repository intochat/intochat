using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.UI;
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
}
