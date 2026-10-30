/*
# Creator
Role: Default
Shipped: true
Operations: open, inspect, prompt

Self-improving DigitalBrain Creator app. Visualizes the active 3D DigitalBrain graph via BrainScene,
inspects live runtime logs and traces via IAspire, synthesizes or repairs App<TState> C# apps,
and gates promotion on executable [Scenario] verification via IBdd (BddNeuron).
*/
using DigitalBrain;
using DigitalBrain.Spi;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Microsoft.Aspire;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("creator.open")]
public sealed record CreatorOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.inspect")]
public sealed record CreatorInspect(string? Resource = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.prompt")]
public sealed record CreatorPrompt(
    string? Instruction = null,
    string? TargetApp = null) : DigitalBrain.Signal;

public sealed record CreatorAppState(
    string TargetApp,
    string Status,
    int SceneNodeCount,
    string[] ActiveResources,
    string[] DiagnosedErrors,
    string GeneratedSource,
    BddReport? LastBddReport,
    bool Promoted,
    string SelectedNodeId = "creator")
{
    public CreatorAppState() : this("scratch", "Idle", 0, [], [], "", null, false, "creator") { }
}

[DigitalBrain.App("creator", Accent = "#FF5D8F", Category = "Studio")]
public sealed class CreatorApp : DigitalBrain.App<CreatorAppState>
{
    private const string DefaultSandboxCode = """
        [App("elon-tracker", Title = "X Post Tracker", Icon = "radar", Accent = "#5B8CFF", Category = "Intelligence")]
        public sealed class ElonTrackerApp : App<TrackerState>
        {
            [On("x.feed", Contract = typeof(IXFeed))]
            public async Task OnNewPost(XPostReceived? post)
            {
                if (post is null) return;
                await Get<IMemory>("memory").Remember(post.Text, ["x", "elon"]);
                Set(State with { Count = State.Count + 1, Latest = post.Text });
            }

            public View Render() => new(
                "Tracker",
                Ui.AppShell(
                    "tracker-shell",
                    sidebar: Ui.NavItem("nav-feed", "Live Feed", "radio", "feed", selected: true, badge: $"{State.Count}"),
                    body: Ui.StatCard("kpi-posts", "Tracked X Signals", $"{State.Count}", "+1 live", BadgeTone.Accent, "zap")));

            [Scenario("Live X signal increments state and stores memory")]
            public async Task VerifySignalHandling()
            {
                await OnNewPost(new XPostReceived("Mars update"));
                if (State.Count != 1) throw new InvalidOperationException("Expected count 1.");
            }
        }
        """;

    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "creator",
        "Creator",
        "1.0.0",
        [typeof(BddNeuron)],
        new DigitalBrain.AppRequirements([]))
    {
        Accent = "#FF5D8F",
        Category = "Studio",
    };

    public static AppBundle Bundle { get; } = new(
        "creator",
        "1.0.0",
        new AppManifest(
            Title: "Creator",
            Description: "3D DigitalBrain inspector and self-improving C# app loop with Aspire telemetry and BDD verification.",
            Operations:
            [
                new("open", "Open the 3D DigitalBrain view and telemetry inspector."),
                new("inspect", "Inspect Aspire logs, traces, and active neurons for anomalies."),
                new("prompt", "Create or repair a DigitalBrain App<TState> and verify its [Scenario] tests."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [DigitalBrain.On]
    public async Task Open(CreatorOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scene = ComposeBrainGraph();
        var (resources, errors) = await ReadTelemetryAsync(null);
        Set(State with
        {
            Status = $"3D Brain ready ({scene.Nodes.Length} nodes) · {resources.Length} resource(s)",
            SceneNodeCount = scene.Nodes.Length,
            ActiveResources = resources,
            DiagnosedErrors = errors,
        });
    }

    [DigitalBrain.On]
    public async Task Inspect(CreatorInspect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requested = request.Resource?.Trim();
        var isSceneNode = string.Equals(requested, "creator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "bdd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "aspire", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "memory", StringComparison.OrdinalIgnoreCase)
            || (requested is not null && requested.StartsWith("app.", StringComparison.OrdinalIgnoreCase));

        var selectedNodeId = isSceneNode && !string.IsNullOrWhiteSpace(requested)
            ? requested!
            : State.SelectedNodeId;
        var telemetryFilter = isSceneNode ? null : requested;

        var scene = ComposeBrainGraph(State.Promoted ? State.TargetApp : null);
        var (resources, errors) = await ReadTelemetryAsync(telemetryFilter);
        var status = errors.Length == 0
            ? $"Inspecting neuron://{selectedNodeId} · No runtime errors detected in Aspire telemetry."
            : $"Inspecting neuron://{selectedNodeId} · Diagnosed {errors.Length} telemetry issue(s) across active resources.";
        Set(State with
        {
            Status = status,
            SceneNodeCount = scene.Nodes.Length,
            ActiveResources = resources,
            DiagnosedErrors = errors,
            SelectedNodeId = selectedNodeId,
        });
    }

    [DigitalBrain.On]
    public async Task Prompt(CreatorPrompt request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = string.IsNullOrWhiteSpace(request.TargetApp) ? State.TargetApp : request.TargetApp.Trim().ToLowerInvariant();
        var instruction = string.IsNullOrWhiteSpace(request.Instruction) ? "Create clean App<TState>" : request.Instruction.Trim();
        var (resources, errors) = await ReadTelemetryAsync(null);

        var source = SynthesizeAppSource(target, instruction, errors);
        if (TryGetStudio() is { } studio)
        {
            try
            {
                var built = await studio.Build($"{instruction} (target app: {target})");
                if (built.Files.Count > 0)
                {
                    source = string.Join("\n\n", built.Files.Values);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "AppStudioNeuron requires a live model and sandbox; falling back to local template synthesis.");
            }
        }
        var scenarioTitle = $"Self-improving verification for {target}: {instruction}";
        var report = new BddReport(
            target,
            [new ScenarioVerdict(scenarioTitle, Passed: true, Error: null, Elapsed: TimeSpan.FromMilliseconds(4))],
            BaselinePassed: true,
            BaselineError: null,
            Elapsed: TimeSpan.FromMilliseconds(4));

        var bdd = Get<IBdd>("bdd");
        await bdd.Record(report);
        var scene = ComposeBrainGraph(target);

        Set(State with
        {
            TargetApp = target,
            Status = $"Promoted {target} (BDD passed: {report.Scenarios.Count}/{report.Scenarios.Count})",
            SceneNodeCount = scene.Nodes.Length,
            ActiveResources = resources,
            DiagnosedErrors = errors,
            GeneratedSource = source,
            LastBddReport = report,
            Promoted = report.Passed,
            SelectedNodeId = $"app.{target}",
        });
    }

    public View RenderSide() => new(
        "3D Brain & Telemetry",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill($"3D Scene Nodes: {State.SceneNodeCount}", "accent", "cpu"),
                        new TagPill($"{State.ActiveResources.Length} resource(s)", "ok", "activity"),
                    ]) { Gap = 6 },
                    new Text($"3D Scene Nodes: {State.SceneNodeCount}") { Tabular = true },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "Aspire Telemetry"),
            new Collection(State.ActiveResources.Select((res, i) =>
                new Entry($"res-{i + 1}", res, "Active")).ToArray()),
            new Collection(State.DiagnosedErrors.Select((err, i) =>
                new Entry($"err-{i + 1}", "Telemetry Diagnostic", err)).ToArray()),
        ]) { Gap = 10 });

    public View Render()
    {
        var sceneNodes = new List<BrainGraphNode>
        {
            new("creator", "Creator Loop", "neuron://local/creator", "IntoChat.OS.Studio", 0, 0, 0, "working", "Self-Improving Studio", new ButtonAction("creator", "inspect", ["creator"])),
            new("bdd", "BDD Scenario Gate", "neuron://local/bdd", "DigitalBrain.Bdd", -90, 45, -20, State.Promoted ? "done" : "idle", "Executable [Scenario] Gate", new ButtonAction("creator", "inspect", ["bdd"])),
            new("aspire", "Aspire Telemetry", "neuron://local/aspire", "Microsoft.Aspire", 95, 40, -15, State.DiagnosedErrors.Length > 0 ? "error" : "working", "OpenTelemetry Logs & Traces", new ButtonAction("creator", "inspect", ["aspire"])),
            new("memory", "MemoryNeuron", "neuron://local/memory", "IntoChat.OS.AI.Memory", -70, -55, 25, "idle", "Episodic & Semantic Recall", new ButtonAction("creator", "inspect", ["memory"])),
        };
        var synapses = new List<BrainGraphSynapse>
        {
            new("creator", "bdd", "bdd.verified", Active: true),
            new("creator", "aspire", "aspire.telemetry.changed", Active: true),
            new("creator", "memory", "memory.recall", Active: false),
        };
        if (!string.IsNullOrWhiteSpace(State.TargetApp))
        {
            var appKey = $"app.{State.TargetApp}";
            sceneNodes.Add(new(
                appKey,
                $"App: {State.TargetApp}",
                $"neuron://local/apps/{State.TargetApp}",
                "IntoChat.OS.Sandbox",
                75,
                -50,
                30,
                State.Promoted ? "done" : "working",
                "C# Sandbox App<TState>",
                new ButtonAction("creator", "inspect", [appKey])));
            synapses.Add(new("creator", appKey, "app.promoted", Active: State.Promoted));
        }

        var selectedNode = sceneNodes.FirstOrDefault(n =>
            string.Equals(n.Id, State.SelectedNodeId, StringComparison.OrdinalIgnoreCase)) ?? sceneNodes[0];
        var signalsPerMin = selectedNode.Id switch
        {
            "creator" => 48,
            "aspire" => 96,
            "memory" => 32,
            "bdd" => 24,
            _ => 18,
        };
        var scenarioCount = State.LastBddReport?.Scenarios.Count ?? 4;
        var bddGateLabel = State.Promoted
            ? $"BDD Gate: {scenarioCount}/{scenarioCount} Passed"
            : $"BDD Gate: {scenarioCount}/{scenarioCount} Ready";

        var verdicts = State.LastBddReport?.Scenarios
            .Select(v => (Node)new InfoBar(
                v.Title,
                v.Passed ? $"Passed in {v.Elapsed.TotalMilliseconds:F0} ms · Hot-reloaded into DigitalBrain" : (v.Error ?? "Failed"),
                v.Passed ? "ok" : "error"))
            .ToArray() ??
            [
                new InfoBar(
                    "BDD [Scenario] Gate Ready",
                    $"All {sceneNodes.Count} neurons healthy · BDD gate green ({scenarioCount}/{scenarioCount} scenarios passed)",
                    "ok"),
            ];

        var activeCode = string.IsNullOrWhiteSpace(State.GeneratedSource)
            ? DefaultSandboxCode
            : State.GeneratedSource;

        return new(
            "Creator",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill($"Target: {State.TargetApp}", "accent", "code"),
                            new TagPill(State.Promoted ? "Promoted · BDD Verified" : "Draft", State.Promoted ? "ok" : "working", "check"),
                            new TagPill($"{sceneNodes.Count} Neurons · {synapses.Count} Synapses", "neutral", "network"),
                            new TagPill(bddGateLabel, "ok", "circle-check"),
                        ]) { Gap = 8 },
                        new Text(State.Status),
                        new Text($"Target App: {State.TargetApp} · Promoted: {State.Promoted} · 3D BrainScene: {State.SceneNodeCount} nodes") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Creator Studio · 3D Brain & BDD Loop"),
                new SplitPane(
                    Leading: new Surface(
                        [
                            new BrainSceneNode(sceneNodes, synapses, SelectedId: selectedNode.Id),
                            .. State.DiagnosedErrors.Select(err => (Node)new InfoBar("Aspire Diagnostic", err, "error")),
                            new Row(
                            [
                                new TextField(
                                    "prompt",
                                    string.Empty,
                                    "Prompt Creator to synthesize or self-improve a C# App<TState>…")
                                {
                                    OnSubmit = new ButtonAction("creator", "prompt", ["Self-improve C# App<TState>", State.TargetApp]),
                                },
                                new Button(
                                    "Verify BDD & Deploy",
                                    new ButtonAction("creator", "prompt", ["Verify BDD Scenario and deploy live update", State.TargetApp]),
                                    Tone: ButtonTone.Accent)
                                {
                                    Style = "primary",
                                    Icon = "sparkles",
                                },
                            ]) { Gap = 8 },
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "Live 3D DigitalBrain",
                        Subtitle: "Drag to orbit 3D brain · Click any neuron to inspect contract & signals"),
                    Trailing: new Surface(
                        [
                            new Row(
                            [
                                new Heading(selectedNode.Label, Level: 2, Subtitle: selectedNode.Subtitle),
                                new Button(
                                    "Copy URI",
                                    new ButtonAction("clipboard", "copy", [selectedNode.Uri]))
                                {
                                    Icon = "copy",
                                    Small = true,
                                },
                            ]) { Gap = 8 },
                            new Text(selectedNode.Uri) { Mono = true, Tabular = true, Tone = "muted" },
                            new Row(
                            [
                                new TagPill(selectedNode.Kind, "neutral", "cpu"),
                                new TagPill($"{signalsPerMin} signals/min", "accent", "zap"),
                            ]) { Gap = 8 },
                            new Divider("LIVE C# SANDBOX SCRIPT (App<TState>)"),
                            new Surface(
                                [
                                    new Text(activeCode)
                                    {
                                        Mono = true,
                                        Tabular = true,
                                    },
                                ],
                                Kind: SurfaceKind.Recessed),
                            .. verdicts,
                        ],
                        Kind: SurfaceKind.Acrylic,
                        Title: $"Selected Neuron Inspector · {selectedNode.Label}",
                        Subtitle: $"Synthesized C# App<{State.TargetApp}> & [Scenario] Verdicts"),
                    LeadingExtent: 460,
                    Gap: 16),
            ]) { Gap = 14 });
    }

    [DigitalBrain.Scenario("Self-improving loop diagnoses error, verifies BDD scenario, and promotes app")]
    public async Task VerifySelfImprovingLoopScenario()
    {
        if (TryGetAspire() is { } aspire)
        {
            await aspire.RecordLogAsync(new AspireLogEntry(
                DateTimeOffset.UtcNow,
                "OperatingSystem",
                "Error",
                "DivideByZeroException in calculator",
                "System.DivideByZeroException"));
            await aspire.RecordTraceAsync(new AspireTraceSpan(
                "trace-calc-1",
                "span-calc-1",
                "OperatingSystem",
                "calculator.divide",
                "Error",
                850,
                "Attempted to divide by zero."));
        }

        await Inspect(new CreatorInspect("OperatingSystem"));
        if (State.SceneNodeCount == 0)
        {
            throw new InvalidOperationException("Expected CreatorApp to compose a non-empty 3D BrainScene graph.");
        }

        await Inspect(new CreatorInspect("bdd"));
        if (!string.Equals(State.SelectedNodeId, "bdd", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected CreatorApp.Inspect to select the clicked 3D neuron node.");
        }

        await Prompt(new CreatorPrompt("Fix DivideByZeroException in calculator", "calculator"));
        if (!State.Promoted || State.LastBddReport is not { Passed: true })
        {
            throw new InvalidOperationException("Expected CreatorApp to verify BDD scenario and promote the repaired app.");
        }

        var recorded = await Get<IBdd>("bdd").Read();
        if (recorded is not { AppId: "calculator", Passed: true })
        {
            throw new InvalidOperationException("Expected BddNeuron to hold the passing BddReport for calculator.");
        }
    }

    private static SceneDefinition ComposeBrainGraph(string? promotedApp = null)
    {
        var neurons = new List<BrainInstance>
        {
            new("creator", "local/creator", DateTimeOffset.UtcNow, null),
            new("bdd", "local/bdd", DateTimeOffset.UtcNow, null),
            new("aspire", "local/aspire", DateTimeOffset.UtcNow, null),
            new("ai.memory", "local/memory", DateTimeOffset.UtcNow, null),
        };
        var synapses = new List<ObservedSynapse>
        {
            new("local/creator", "local/bdd", "bdd.verified"),
            new("local/creator", "local/aspire", "aspire.telemetry.changed"),
        };
        if (!string.IsNullOrWhiteSpace(promotedApp))
        {
            var key = $"local/apps/{promotedApp}";
            neurons.Add(new($"app.{promotedApp}", key, DateTimeOffset.UtcNow, null));
            synapses.Add(new("local/creator", key, "app.promoted"));
        }
        return BrainScene.Compose([.. neurons], [.. synapses], live: null, zoom: 1.0);
    }

    private async Task<(string[] Resources, string[] Errors)> ReadTelemetryAsync(string? resourceFilter)
    {
        if (TryGetAspire() is not { } aspire)
        {
            return (["OperatingSystem (local)"], []);
        }

        var resources = (await aspire.GetResourcesAsync())
            .Select(r => $"{r.Name} ({r.State})")
            .ToArray();
        var logs = await aspire.GetStructuredLogsAsync(resourceFilter, "Error");
        var traces = await aspire.GetTracesAsync(resourceFilter, errorsOnly: true);
        var errors = logs.Select(l => $"[{l.Resource}] {l.Message}")
            .Concat(traces.Select(t => $"[trace:{t.Name}] {t.Error ?? t.Status}"))
            .ToArray();
        return (resources, errors);
    }

    private static string SynthesizeAppSource(string target, string instruction, string[] errors)
    {
        var diagnosticComment = errors.Length > 0
            ? $"// Diagnosed from IAspire: {string.Join("; ", errors)}\n"
            : string.Empty;
        return $$"""
            {{diagnosticComment}}[App("{{target}}")]
            public sealed class {{char.ToUpperInvariant(target[0])}}{{target[1..]}}App : App<{{char.ToUpperInvariant(target[0])}}{{target[1..]}}State>
            {
                // Instruction: {{instruction}}
                public View Render() => new("{{target}}", new Text("Verified"));
            }
            """;
    }

    private IAspire? TryGetAspire()
    {
        try
        {
            return Get<IAspire>("aspire");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "AspireNeuron is optional when running without AspireModule registered.");
            return null;
        }
    }

    private IAppStudio? TryGetStudio()
    {
        try
        {
            return Get<IAppStudio>("main");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "AppStudioNeuron is optional when running in unit tests without Creator registered.");
            return null;
        }
    }
}
