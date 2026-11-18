/*
# Reference DDD Reactor Screens
Role: Default
Shipped: false

Reference aggregates and AggregateScreen<TState> implementations proving Phase 0 C# Core:
1. HelloScreen (~20 lines, Hello aggregate + Toggle)
2. BankAccount + BankScreen (~30 lines, Accordant-style Empty -> Open -> Deposit -> Withdraw state graph + SufficientFunds rule guard)
3. SearchableCatalogScreen (Arg.Text typed field binding + pure TState transitions + async screen orchestration)
*/
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Sandbox;
using IntoChat.OS.UI;
using IntoChat.Sdk;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Tests.Unit.UI;

// ── 1. Hello World Reference Aggregate & Screen (~20 lines) ─────────────────

[Signal("hello.toggled")]
public sealed record HelloToggled(bool Shown) : Signal;

public sealed record Hello(bool Shown = false)
{
    public Hello() : this(false) { }

    public Decision<Hello> Toggle() =>
        Decision.Accept(this with { Shown = !Shown }, new HelloToggled(!Shown));
}

[Neuron("app.test.hello")]
[Screen("app.test.hello", Title = "Hello World", Icon = Icons.Sparkles)]
public sealed class HelloScreen : AggregateScreen<Hello>
{
    protected override Node Render(Hello s) =>
        Card("Hello World")[
            VStack(Space.Sm)[
                s.Shown ? Text("Hello, IntoChat OS!") : MutedText("Click below to greet."),
                PrimaryButton(s.Shown ? "Hide" : "Say Hello")
                    .OnClick<Hello>(x => x.Toggle())
            ]
        ];
}

// ── 2. Accordant-Inspired BankAccount Reference Aggregate & Screen ──────────

[Signal("bank.opened")]
public sealed record AccountOpened(string Holder) : Signal;

[Signal("bank.deposited")]
public sealed record Deposited(decimal Amount, decimal Balance) : Signal;

[Signal("bank.withdrawn")]
public sealed record Withdrawn(decimal Amount, decimal Balance) : Signal;

public sealed record BankAccount(bool IsOpen = false, string Holder = "", decimal Balance = 0m)
{
    public BankAccount() : this(false, "", 0m) { }

    [Invariant("Account balance can never drop below zero.")]
    public bool NonNegativeBalance => Balance >= 0m;

    [Invariant]
    public Rule OpenAccountHasHolder => Decision.Require(
        "holder_present_when_open",
        this,
        static a => !a.IsOpen || !string.IsNullOrWhiteSpace(a.Holder),
        "An open bank account must have a holder.");

    public Rule MustNotBeOpen => Decision.Rule("account_open", this, static a => a.IsOpen, "Account is already open.");
    public Rule MustBeOpen => Decision.Rule("not_open", this, static a => !a.IsOpen, "Open the account first.");
    public Rule PositiveAmount(decimal amount) => Decision.Rule("positive_amount", amount, static a => a <= 0m, "Amount must be positive.");
    public Rule SufficientFunds(decimal amount) => Decision.Rule(
        "sufficient_funds",
        (Account: this, Amount: amount),
        static x => !x.Account.IsOpen || x.Account.Balance < x.Amount,
        static x => $"Insufficient funds (balance {x.Account.Balance:C0}, requested {x.Amount:C0}).");

    public Decision<BankAccount> Open([Sample("alice")] string holder) =>
        Decision.Check("open", MustNotBeOpen, Decision.Rule("holder_required", holder, static h => string.IsNullOrWhiteSpace(h), "Holder is required."))
        ?? Decision.Accept(new BankAccount(true, holder, 0m), new AccountOpened(holder));

    public Decision<BankAccount> Deposit([Sample(50)] decimal amount) =>
        Decision.Check("deposit", MustBeOpen, PositiveAmount(amount))
        ?? Decision.Accept(this with { Balance = Balance + amount }, new Deposited(amount, Balance + amount));

    public Decision<BankAccount> Withdraw([Sample(30, 70)] decimal amount) =>
        Decision.Check("withdraw", MustBeOpen, PositiveAmount(amount), SufficientFunds(amount))
        ?? Decision.Accept(this with { Balance = Balance - amount }, new Withdrawn(amount, Balance - amount));

    public override string ToString() => IsOpen ? $"{Holder}: ${Balance:0.##}" : "Empty";
}

[Neuron("app.test.bank")]
[Screen("app.test.bank", Title = "Bank Account", Icon = Icons.Landmark)]
public sealed class BankScreen : AggregateScreen<BankAccount>
{
    protected override Node Render(BankAccount a) =>
        Card("Bank Account", a.IsOpen ? a.Holder : "Closed")[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    Metric("Balance", $"${a.Balance:0.00}"),
                    StatusPill(a.IsOpen ? StatusKind.Done : StatusKind.Idle, a.IsOpen ? "Open" : "Closed")
                ],
                LastRejection is { } r
                    ? Banner("Refused", string.Join(" ", r.Messages), tone: "error")
                    : null,
                !a.IsOpen
                    ? PrimaryButton("Open for Alice").OnClick<BankAccount>(x => x.Open("alice"))
                    : HStack(Space.Sm)[
                        PrimaryButton("+$50").OnClick<BankAccount>(x => x.Deposit(50m)),
                        Button("-$30").OnClick<BankAccount>(x => x.Withdraw(30m)).Guard(a.SufficientFunds(30m)),
                        DangerButton("-$70").OnClick<BankAccount>(x => x.Withdraw(70m)).Guard(a.SufficientFunds(70m))
                    ]
            ]
        ];
}

// ── 3. Typed Field Binding, Pure UI State, and Async Screen Orchestration ───

[DefaultNeuronType("test.fx.oracle")]
public interface IFxOracle
{
    Task<decimal> GetRate();
}

[Neuron("test.fx.oracle")]
public sealed class FxOracleNeuron : DigitalBrain.Neurons.Neuron, IFxOracle
{
    public Task<decimal> GetRate() => Task.FromResult(1.25m);
}

[Signal("workspace.rate.synced")]
public sealed record RateSynced(decimal Rate) : Signal;

public sealed record WorkspaceFilterState(
    string Query = "",
    bool SidebarOpen = true,
    string SelectedTab = "overview",
    decimal SyncedRate = 1.0m)
{
    public WorkspaceFilterState() : this("", true, "overview", 1.0m) { }

    public WorkspaceFilterState ToggleSidebar() => this with { SidebarOpen = !SidebarOpen };

    public WorkspaceFilterState SelectTab(string tab) => this with { SelectedTab = tab };

    public WorkspaceFilterState SetQuery(string query) => this with { Query = query.Trim() };

    public Decision<WorkspaceFilterState> ApplyRate(decimal rate) =>
        Decision.Check(Decision.Rule("positive_rate", rate, static r => r <= 0m, "Exchange rate must be positive."))
        ?? Decision.Accept(this with { SyncedRate = rate }, new RateSynced(rate));
}

[Neuron("app.test.workspace-filter")]
[Screen("app.test.workspace-filter", Title = "Workspace Filter")]
public sealed class WorkspaceFilterScreen : AggregateScreen<WorkspaceFilterState>
{
    public async Task SyncExchangeRateAsync()
    {
        var rate = await Get<IFxOracle>("default").GetRate();
        await ApplyDecisionAsync(State.ApplyRate(rate));
    }

    protected override Node Render(WorkspaceFilterState s) =>
        VStack(Space.Md)[
            HeaderBar(
                "Workspace",
                $"Rate {s.SyncedRate:0.00} · {(s.SidebarOpen ? "Expanded" : "Compact")}",
                IconButton(Icons.PanelLeft, "Toggle Sidebar").OnClick<WorkspaceFilterState>(x => x.ToggleSidebar()),
                GhostButton("Sync Rate", Icons.RefreshCw).OnClick(() => SyncExchangeRateAsync())),
            Tabs(
                s.SelectedTab,
                Tab("overview", "Overview").OnSelect<WorkspaceFilterState>(x => x.SelectTab("overview")),
                Tab("graph", "Graph").OnSelect<WorkspaceFilterState>(x => x.SelectTab("graph"))),
            SearchField("Filter items…")
                .Key("search-box")
                .OnChange<WorkspaceFilterState>(x => x.SetQuery(Arg.Text)),
            MutedText($"Query: {s.Query}"),
            Slot("neuron://local/app.test.hello/default")
        ];
}

// ── Test Suite ──────────────────────────────────────────────────────────────

public sealed class DddReactorAndExplorerFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("app.test.hello")]
    [InlineData("app.test.bank")]
    [InlineData("app.test.workspace-filter")]
    public async Task AggregateScreensHandleEmptyJsonRequestAndStateInspectionCleanly(string alias)
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register<HelloScreen>();
        os.Register<BankScreen>();
        os.Register<WorkspaceFilterScreen>();

        var id = new NeuronId(new BrainId("test"), alias, "default");
        var screen = os.Get<IScreen>(id);

        var initial = await screen.Read();
        Assert.NotNull(initial);

        using var emptyDoc = JsonDocument.Parse("{}");
        await screen.Request(UiCommand.SignalAlias, emptyDoc.RootElement);
        await os.Idle();
        Assert.Empty(os.Failures);

        var post = await screen.Read();
        Assert.NotNull(post);
        Assert.NotNull(await ((IBrainObserver)os).State(id, Ct));
    }

    [Fact]
    public async Task HelloScreenEndToEndToggleUpdatesStateViewAndJournal()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register<HelloScreen>();

        var brain = new BrainId("test");
        var id = new NeuronId(brain, "app.test.hello", "default");
        var screen = os.Get<IScreen>(id);

        var initial = await screen.Open();
        Assert.NotNull(initial);
        Assert.Equal(1, initial!.Generation);
        Assert.Contains("Click below to greet.", initial.View.GetRawText(), StringComparison.Ordinal);

        // Activate via deterministic expression-derived key cmd:Toggle
        await screen.Activate("app.test.hello", initial.Generation, "cmd:Toggle");
        await os.Idle();

        var toggled = await screen.Open();
        Assert.NotNull(toggled);
        Assert.Equal(2, toggled!.Generation);
        Assert.Contains("Hello, IntoChat OS!", toggled.View.GetRawText(), StringComparison.Ordinal);

        var signals = os.Journal(brain).Signals(id);
        Assert.Contains(signals, entry => entry.Signal is HelloToggled { Shown: true });
    }

    [Fact]
    public void BankAccountExplorerUnfoldsAccordantStateGraphVerifiesInvariantsAndChecksGuardParity()
    {
        var choices = new ChoiceSet()
            .ForParameter("holder", "alice")
            .ForParameter("amount", 50m, 30m, 70m);

        var graph = AggregateExplorer<BankAccount>.ExploreScreen<BankScreen>(choices, maxStates: 16);
        graph.EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.Empty(graph.Violations);

        // Verify expected Accordant states are discovered: Empty, alice: $0, alice: $50, alice: $20, etc.
        var labels = graph.States.Select(s => s.Label).ToList();
        Assert.Contains("Empty", labels);
        Assert.Contains("alice: $0", labels);
        Assert.Contains("alice: $50", labels);
        Assert.Contains("alice: $20", labels);

        // Verify forward accepted edges emit domain signals
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Open(\"alice\")" && t.Signals.Contains("bank.opened"));
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Deposit(50)" && t.Signals.Contains("bank.deposited"));
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Withdraw(30)" && t.Signals.Contains("bank.withdrawn"));

        // Verify self-loop Rejected transitions match Accordant's bank-account-state-graph rules!
        Assert.Contains(graph.RejectedTransitions, t =>
            t.FromId == t.ToId && t.BrokenRules.Contains("not_open"));
        Assert.Contains(graph.RejectedTransitions, t =>
            t.FromId == t.ToId && t.BrokenRules.Contains("account_open"));
        Assert.Contains(graph.RejectedTransitions, t =>
            t.FromId == t.ToId && t.Command == "Withdraw(70)" && t.BrokenRules.Contains("sufficient_funds"));

        // Verify StateGraphView converts the unfolded graph into a wire-valid BrainSceneNode + DOT export
        var scene = StateGraphView(graph);
        Assert.Equal(graph.States.Count, scene.Nodes.Count);
        Assert.NotEmpty(scene.Synapses);
        Assert.True(scene.PauseRotation);

        var dot = graph.ToDot();
        Assert.Contains("digraph BankAccount", dot, StringComparison.Ordinal);
        Assert.Contains("sufficient_funds", dot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BankScreenGuardDisablesIllegalButtonAndDirectInvocationRefusesWithSameStateAndEphemeralRejected()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register<BankScreen>();

        var rejectedHeard = new List<Rejected>();
        await using var sub = await os.ClusterListeners.Listen<Rejected>(
            "test-rejected-listener",
            (_, signal, _) =>
            {
                rejectedHeard.Add(signal);
                return Task.CompletedTask;
            });

        var brain = new BrainId("test");
        var id = new NeuronId(brain, "app.test.bank", "acct-1");
        var screen = os.Get<IScreen>(id);

        // 1. Open account -> alice: $0
        var v1 = (await screen.Open())!;
        await screen.Activate("app.test.bank", v1.Generation, "cmd:Open:alice");
        await os.Idle();

        // 2. Deposit $50 -> alice: $50
        var v2 = (await screen.Open())!;
        await screen.Activate("app.test.bank", v2.Generation, "cmd:Deposit:50");
        await os.Idle();

        var v3 = (await screen.Open())!;
        var v3Json = v3.View.GetRawText();
        Assert.Contains("$50.00", v3Json, StringComparison.Ordinal);

        // Inspect button states at $50: -$30 is enabled, -$70 is disabled by .Guard(a.SufficientFunds(70m)) AND carries wire guardReason tooltip!
        var btn30 = FindNodeByKey(v3.View, "cmd:Withdraw:30");
        var btn70 = FindNodeByKey(v3.View, "cmd:Withdraw:70");
        Assert.True(btn30.GetProperty("enabled").GetBoolean());
        Assert.False(btn70.GetProperty("enabled").GetBoolean());
        Assert.Contains(
            "Insufficient funds",
            btn70.GetProperty("emit").GetProperty("args").GetProperty("guardReason").GetString(),
            StringComparison.Ordinal);

        // 3. Bypass UI disabled flag via direct Screen.Activate / MCP on cmd:Withdraw:70 -> server enforces exact same Rule!
        await screen.Activate("app.test.bank", v3.Generation, "cmd:Withdraw:70");
        await os.Idle();

        // Ephemeral Rejected signal published to cluster listeners
        var rejection = Assert.Single(rejectedHeard);
        Assert.Equal("withdraw", rejection.Operation);
        Assert.Contains("sufficient_funds", rejection.Rules);

        // State MUST remain at $50 (SameState) and zero Withdrawn signals in journal!
        var stateJson = await ((IBrainObserver)os).State(id, Ct);
        Assert.NotNull(stateJson);
        var persisted = stateJson!.Value.Deserialize<BankAccount>(RuntimeProtocol.OptionsFor(typeof(BankAccount)))!;
        Assert.Equal(50m, persisted.Balance);
        Assert.DoesNotContain(os.Journal(brain).Signals(id), s => s.Signal is Withdrawn);

        // Because BankScreen renders LastRejection inline via Banner("Refused", ...), the view updates to show the refusal banner
        var v4 = (await screen.Open())!;
        Assert.Contains("\"kind\":\"infoBar\"", v4.View.GetRawText(), StringComparison.Ordinal);

        // 4. Valid -$30 withdrawal succeeds, clears LastRejection banner, and journals Withdrawn(30, 20)
        await screen.Activate("app.test.bank", v4.Generation, "cmd:Withdraw:30");
        await os.Idle();

        var v5 = (await screen.Open())!;
        Assert.Contains("$20.00", v5.View.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":\"infoBar\"", v5.View.GetRawText(), StringComparison.Ordinal);
        // And at $20, BOTH -$30 and -$70 buttons are now disabled with guardReason tooltips!
        Assert.False(FindNodeByKey(v5.View, "cmd:Withdraw:30").GetProperty("enabled").GetBoolean());
        Assert.Contains(
            "Insufficient funds",
            FindNodeByKey(v5.View, "cmd:Withdraw:30").GetProperty("emit").GetProperty("args").GetProperty("guardReason").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(os.Journal(brain).Signals(id), s => s.Signal is Withdrawn { Amount: 30m, Balance: 20m });
    }

    [Fact]
    public async Task TypedFieldBindingPureStateTransitionsAndAsyncOrchestrationWorkEndToEnd()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register<WorkspaceFilterScreen>();
        os.Register<FxOracleNeuron>();

        var brain = new BrainId("test");
        var id = new NeuronId(brain, "app.test.workspace-filter", "main");
        var screen = os.Get<IScreen>(id);

        var v1 = (await screen.Open())!;
        var savesAfterOpen = os.Journal(brain).Signals(id).Count;

        // 1. Pure TState transition (ToggleSidebar) updates state + view with zero domain events
        await screen.Activate("app.test.workspace-filter", v1.Generation, "cmd:ToggleSidebar");
        await os.Idle();

        var v2 = (await screen.Open())!;
        Assert.Equal(v1.Generation + 1, v2.Generation);
        var stateAfterToggle = (await ((IBrainObserver)os).State(id, Ct))!.Value
            .Deserialize<WorkspaceFilterState>(RuntimeProtocol.OptionsFor(typeof(WorkspaceFilterState)))!;
        Assert.False(stateAfterToggle.SidebarOpen);
        // Only ViewChanged was appended, no domain signal
        Assert.Equal(savesAfterOpen + 1, os.Journal(brain).Signals(id).Count);

        // 2. Typed field binding: Arg.Text injects fieldValue straight into SetQuery(string query)
        await screen.Activate("app.test.workspace-filter", v2.Generation, "search-box", fieldValue: "  quarterly invoices  ");
        await os.Idle();

        var stateAfterSearch = (await ((IBrainObserver)os).State(id, Ct))!.Value
            .Deserialize<WorkspaceFilterState>(RuntimeProtocol.OptionsFor(typeof(WorkspaceFilterState)))!;
        Assert.Equal("quarterly invoices", stateAfterSearch.Query);

        // 3. Async screen orchestration method (() => SyncExchangeRateAsync()) calls IFxOracle neuron and saves RateSynced
        var v3 = (await screen.Open())!;
        await screen.Activate("app.test.workspace-filter", v3.Generation, "cmd:SyncExchangeRateAsync");
        await os.Idle();

        var v4 = (await screen.Open())!;
        Assert.Contains("Rate 1.25", v4.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(os.Journal(brain).Signals(id), s => s.Signal is RateSynced { Rate: 1.25m });
    }

    [Fact]
    public void ExplorerDetectsBrokenInvariantsAndDuplicateInteractiveKeys()
    {
        // Duplicate interactive keys in Render(s) are flagged immediately
        var dupGraph = AggregateExplorer<Hello>.Explore(
            render: _ => VStack(
                PrimaryButton("One").Key("dup-key").OnClick<Hello>(x => x.Toggle()),
                Button("Two").Key("dup-key").OnClick<Hello>(x => x.Toggle())));

        Assert.False(dupGraph.IsHealthy);
        Assert.Contains(dupGraph.Violations, v => v.Kind == "DuplicateKey" && v.Name == "dup-key");
        Assert.Throws<InvalidOperationException>(dupGraph.EnsureHealthy);
    }

    [Fact]
    public void KitDslEmitsWireCompatibleNodesStatusPillsAndSlotNeuronRefs()
    {
        // Verify StatusPill DESIGN.md §2.5 icon + tone pairing across all 5 StatusKinds
        var done = StatusPill(StatusKind.Done, "Live");
        var working = StatusPill(StatusKind.Working, "Building");
        var error = StatusPill(StatusKind.Error, "Failed");
        var idle = StatusPill(StatusKind.Idle, "Idle");
        var needsYou = StatusPill(StatusKind.NeedsYou, "Approve");

        Assert.Equal(("done", "check"), (done.Tone, done.Icon));
        Assert.Equal(("working", "loader-circle"), (working.Tone, working.Icon));
        Assert.Equal(("error", "triangle-alert"), (error.Tone, error.Icon));
        Assert.Equal(("neutral", null), (idle.Tone, idle.Icon));
        Assert.Equal(("accent", "hand"), (needsYou.Tone, needsYou.Icon));

        // Verify Slot(...) emits NeuronRef("view", uri)
        var slot = Slot("neuron://local/app.usage/default");
        Assert.Equal("view", slot.Kind);
        Assert.Equal("neuron://local/app.usage/default", slot.NeuronUri);

        // Verify full composite tree serializes & round-trips cleanly through DigitalBrain.UI View wire options
        Node root = ResponsiveSplit(
            leading: Surface(SurfaceKind.Acrylic, "Sidebar")[
                VStack(Space.Sm)[
                    done, working, error, idle, needsYou,
                    ToggleSwitch("Auto-sync", true),
                    SelectBox("Sort", "recent", Option("recent", "Recent"), Option("name", "Name")),
                    SecretField("API Key", "sk-...")
                ]
            ],
            trailing: VStack(Space.Lg)[
                Grid(2)[
                    Metric("MRR", "$12,400", "+8%", BadgeTone.Done),
                    slot
                ],
                Divider("Analytics"),
                Table("Invoices", ["ID", "Total"], [["INV-01", "$120.00"]]),
                LineChart("Revenue", [("Jan", 100), ("Feb", 140)], prefix: "$"),
                BarChart("Signups", [("Mon", 12), ("Tue", 19)]),
                Empty("No alerts", "Everything is calm.")
            ]);

        var view = new View("Kit Smoke", BindOwner(root, "app.test.kit"));
        var json = JsonSerializer.Serialize(view, RuntimeProtocol.OptionsFor(typeof(View)));
        Assert.Contains("\"kind\":\"neuronRef\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refKind\":\"view\"", json, StringComparison.Ordinal);

        var deserialized = JsonSerializer.Deserialize<View>(json, RuntimeProtocol.OptionsFor(typeof(View)));
        Assert.NotNull(deserialized);
        Assert.Equal("Kit Smoke", deserialized!.Title);
    }

    [Fact]
    public async Task RoslynAppPackageCompilerAndSandboxHostSupportDddDecisionAndReactorKitParity()
    {
        var compiler = new AppPackageCompiler();
        AppSourceFile[] sources =
        [
            new AppSourceFile("CounterApp.cs", """
                using DigitalBrain.UI;
                using IntoChat.OS.UI;
                using IntoChat.Sdk;
                using static IntoChat.OS.UI.Kit;

                namespace RuntimeSample;

                public sealed record CounterState(int Value = 0)
                {
                    public CounterState() : this(0) { }

                    public Rule MaxFive => Decision.Rule("max_five", this, static s => s.Value >= 5, "Cannot exceed 5.");

                    public Decision<CounterState> Increment(int step) =>
                        Decision.Check("increment", MaxFive)
                        ?? Decision.Accept(this with { Value = Value + step });
                }

                public sealed record CounterSettings;

                [App("reactor-counter", Title = "Reactor Counter", Summary = "Runtime Roslyn Reactor parity")]
                public sealed class ReactorCounterApp : BrainApp<CounterState, CounterSettings>
                {
                    public override View Render() => new(
                        "Reactor Counter",
                        Card("Counter", $"Value: {State.Value}")[
                            HStack(Space.Sm)[
                                PrimaryButton("+2")
                                    .OnClick<CounterState>(x => x.Increment(2))
                                    .Guard(State.MaxFive),
                                Slot("neuron://local/app.usage/default")
                            ]
                        ]);
                }
                """),
        ];

        var package = new AppPackage(
            "reactor-counter",
            "1.0.0",
            "Reactor Counter",
            "Runtime Roslyn Reactor parity",
            "sparkles",
            "Productivity",
            "alice",
            sources,
            AppPackageNeeds.Empty,
            "{}",
            "");

        var compiled = compiler.Compile(package);
        Assert.True(compiled.Succeeded, string.Join(Environment.NewLine, compiled.Errors));

        using var httpClient = new HttpClient();
        var sandbox = new AppSandboxHost(httpClient);
        var deployed = await sandbox.DeployAsync(new DeployAppRequest(
            BrainId: "b1",
            AppKey: "reactor-counter",
            Version: "1.0.0",
            Digest: compiled.EnrichedPackage!.Digest,
            AssemblyBase64: Convert.ToBase64String(compiled.AssemblyBytes)), Ct);

        Assert.NotNull(deployed.InitialViewJson);
        using var initialDoc = JsonDocument.Parse(deployed.InitialViewJson!);
        var btn = FindNodeByKey(initialDoc.RootElement, "cmd:Increment:2");
        Assert.Equal("reactor-counter", btn.GetProperty("emit").GetProperty("owner").GetString());
        var cmdArgsJson = btn.GetProperty("emit").GetProperty("args").GetRawText();

        // Dispatch ui.command three times: 0 -> 2 -> 4 -> refused at 6? Wait: at Value=4, MaxFive (Value >= 5) is false, so 4+2=6; next call at Value=6 is refused!
        var r1 = await sandbox.DispatchAsync(new DispatchAppRequest("b1", "reactor-counter", "signal", UiCommand.SignalAlias, cmdArgsJson), Ct);
        Assert.True(r1.Handled);
        Assert.Contains("\"value\":2", r1.UpdatedStateJson!, StringComparison.OrdinalIgnoreCase);

        var r2 = await sandbox.DispatchAsync(new DispatchAppRequest("b1", "reactor-counter", "signal", UiCommand.SignalAlias, cmdArgsJson), Ct);
        var r3 = await sandbox.DispatchAsync(new DispatchAppRequest("b1", "reactor-counter", "signal", UiCommand.SignalAlias, cmdArgsJson), Ct);
        Assert.Contains("\"value\":6", r3.UpdatedStateJson!, StringComparison.OrdinalIgnoreCase);

        // Now Value >= 5 -> MaxFive rule is broken -> refused, state stays 6 (SameState), and button is disabled in UpdatedViewJson!
        using var v3Doc = JsonDocument.Parse(r3.UpdatedViewJson!);
        Assert.False(FindNodeByKey(v3Doc.RootElement, "cmd:Increment:2").GetProperty("enabled").GetBoolean());

        var refused = await sandbox.DispatchAsync(new DispatchAppRequest("b1", "reactor-counter", "signal", UiCommand.SignalAlias, cmdArgsJson), Ct);
        Assert.True(refused.Handled);
        Assert.Contains("\"value\":6", refused.UpdatedStateJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max_five", refused.ResultJson!, StringComparison.Ordinal);

        var unloaded = await sandbox.UnloadAsync("b1", "reactor-counter", Ct);
        Assert.True(unloaded.Unloaded);
        _ = r2;
    }

    [Fact]
    public void DecisionRulesJudgeDomainValuesAndNoRuleRecordCarriesABoolConstructorParameter()
    {
        var ruleRecords = typeof(Decision).Assembly
            .GetTypes()
            .Where(t => typeof(DigitalBrain.Rules.Rule).IsAssignableFrom(t) && !t.IsAbstract)
            .ToArray();

        Assert.NotEmpty(ruleRecords);
        foreach (var ruleType in ruleRecords)
        {
            var nonCopyCtors = ruleType
                .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Where(c => !(c.GetParameters() is { Length: 1 } p && p[0].ParameterType == ruleType));
            Assert.All(nonCopyCtors, c => Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(bool)));
        }

        var rule = Decision.Rule(
            "positive_budget",
            0m,
            static budget => budget <= 0m,
            static budget => $"Budget {budget} must be positive.");
        Assert.True(rule.Broken);
        Assert.Equal("Budget 0 must be positive.", rule.Message);
    }

    private static JsonElement FindNodeByKey(JsonElement root, string key)
    {
        var stack = new Stack<JsonElement>([root]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (cur.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key)
                {
                    return cur;
                }

                foreach (var prop in cur.EnumerateObject())
                {
                    if (prop.Name is not ("emit" or "args"))
                    {
                        stack.Push(prop.Value);
                    }
                }
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray())
                {
                    stack.Push(item);
                }
            }
        }

        throw new KeyNotFoundException($"Node with key '{key}' not found in view JSON.");
    }
}
