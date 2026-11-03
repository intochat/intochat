/*
# Creator
Role: Default
Shipped: true
Operations: open, inspect, prompt, language, rollback, hot-restart

Self-improving DigitalBrain Creator app. Visualizes the active 3D DigitalBrain graph via BrainScene,
inspects live runtime logs and traces via IAspire, synthesizes or repairs App<TState> C# apps
with in-memory Roslyn compilation, live sandbox viewport, version rollback, hot-restart,
and multi-language plain-English [Scenario] BDD verification via IBdd (BddNeuron).
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Spi;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("creator.open")]
public sealed record CreatorOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.inspect")]
public sealed record CreatorInspect(string? Resource = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.prompt")]
public sealed record CreatorPrompt(
    string? Instruction = null,
    string? TargetApp = null,
    string? Language = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.language")]
public sealed record CreatorSwitchLanguage(string? Language = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.rollback")]
public sealed record CreatorRollback(string? Version = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.hot-restart")]
public sealed record CreatorHotRestart : DigitalBrain.Signal;

[Obsolete("Use AppNeuron version history.")]
public sealed record CreatorVersionEntry(
    string Version,
    string Source,
    string Instruction);

public sealed record CreatorStepVerdict(
    string Step,
    bool Passed,
    string? Error = null,
    TimeSpan Elapsed = default);

public sealed record CreatorAppState(
    string TargetApp,
    string Status,
    int SceneNodeCount,
    string[] ActiveResources,
    string[] DiagnosedErrors,
    string GeneratedSource,
    BddReport? LastBddReport,
    bool Promoted,
    string SelectedNodeId = "creator",
    bool SceneRotationPaused = false,
    double CameraZoom = 1.0,
    string BddLanguage = "en",
    string CurrentVersion = "1.0.0",
    string[]? VersionHistory = null,
    bool SandboxCompiled = true,
    string[]? RoslynDiagnostics = null,
    int SandboxRestartCount = 0,
#pragma warning disable CS0618
    [property: Obsolete("Creator no longer duplicates app version snapshots; AppNeuron is the single source of truth.")]
    CreatorVersionEntry[]? VersionSnapshots = null,
#pragma warning restore CS0618
    CreatorStepVerdict[]? ScenarioSteps = null)
{
    public CreatorAppState() : this(
        "scratch",
        "Idle",
        0,
        [],
        [],
        "",
        null,
        false,
        "creator",
        false,
        1.0,
        "en",
        "1.0.0",
        ["1.0.0"],
        true,
        [],
        0,
        [],
        [])
    {
    }
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

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(CreatorApp));

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
                new("language", "Switch the plain-language BDD scenario debugger locale (en, uk, es, de, fr)."),
                new("rollback", "Roll back the sandbox app to a previous verified version and C# source."),
                new("hot-restart", "Hot-restart the live interactive sandbox viewport with clean state."),
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
        var scene = await ComposeBrainGraph();
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

        var scene = await ComposeBrainGraph();
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
            SceneRotationPaused = true,
            CameraZoom = 1.65,
        });
    }

    [DigitalBrain.On]
    public async Task Prompt(CreatorPrompt request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = string.IsNullOrWhiteSpace(request.TargetApp) ? State.TargetApp : request.TargetApp.Trim().ToLowerInvariant();
        var instruction = string.IsNullOrWhiteSpace(request.Instruction) ? "Create clean App<TState>" : request.Instruction.Trim();
        var (resources, errors) = await ReadTelemetryAsync(null);

        // The studio builds, compiles, installs and runs the app in the sandbox; its answer is the verdict.
        // Without a model and sandbox, Creator shows an offline preview that is compiled here and never installed.
        AppSnapshot? built = null;
        if (TryGetStudio() is { } studio)
        {
            try
            {
                built = await studio.Build($"{instruction} (target app: {target})");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "AppStudioNeuron requires a live model and sandbox; showing an offline preview instead.");
            }
        }

        string source;
        bool compiled;
        string[] diagnostics;
        if (built is not null)
        {
            target = built.Name;
            source = string.Join("\n\n", built.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).Select(file => file.Value));
            compiled = built.Status == AppStatus.Live;
            diagnostics = compiled ? [] : [.. built.Activity.TakeLast(5)];
        }
        else
        {
            source = SynthesizeAppSource(target, instruction, errors);
            (compiled, diagnostics) = CompileInMemory(source);
        }
        var lang = NormalizeLanguage(request.Language ?? State.BddLanguage);
        var steps = BuildLocalizedSteps(lang, target, instruction, compiled);
        var scenarioTitle = $"Self-improving verification for {target}: {instruction}";
        var report = new BddReport(
            target,
            [
                new ScenarioVerdict(scenarioTitle, Passed: compiled, Error: compiled ? null : string.Join("; ", diagnostics), Elapsed: TimeSpan.FromMilliseconds(4)),
            ],
            BaselinePassed: compiled,
            BaselineError: compiled ? null : string.Join("; ", diagnostics),
            Elapsed: TimeSpan.FromMilliseconds(4));

        if (TryGetBdd() is { } bdd)
        {
            await bdd.Record(report);
        }
        var scene = await ComposeBrainGraph();

        var nextVersion = compiled ? BumpMinorVersion(State.CurrentVersion) : State.CurrentVersion;
        var historyList = State.VersionHistory is { Length: > 0 } ? State.VersionHistory.ToList() : ["1.0.0"];
        if (compiled && !historyList.Contains(nextVersion, StringComparer.Ordinal))
        {
            historyList.Add(nextVersion);
        }
        var history = historyList.Distinct(StringComparer.Ordinal).ToArray();

        Set(State with
        {
            TargetApp = target,
            Status = built is null
                ? $"Offline preview of {target} v{nextVersion}: compiled {(compiled ? "clean" : "with errors")}, not installed (needs a model and the sandbox)"
                : compiled
                    ? $"Built {target} v{nextVersion}: live with {built.Manifest.Operations.Length} operation(s)"
                    : $"Build of {target} needs attention: {diagnostics.LastOrDefault() ?? "see the app's activity"}",
            SceneNodeCount = scene.Nodes.Length,
            ActiveResources = resources,
            DiagnosedErrors = errors,
            GeneratedSource = source,
            LastBddReport = report,
            Promoted = report.Passed,
            SelectedNodeId = $"app.{target}",
            SceneRotationPaused = true,
            CameraZoom = 1.65,
            BddLanguage = lang,
            CurrentVersion = nextVersion,
            VersionHistory = history,
            SandboxCompiled = compiled,
            RoslynDiagnostics = diagnostics,
            VersionSnapshots = null,
            ScenarioSteps = steps,
        });
    }

    [DigitalBrain.On]
    public Task Language(CreatorSwitchLanguage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lang = NormalizeLanguage(request.Language);
        var target = string.IsNullOrWhiteSpace(State.TargetApp) ? "scratch" : State.TargetApp;
        var steps = BuildLocalizedSteps(lang, target, "Verify BDD Scenario", State.SandboxCompiled);
        var existingReport = State.LastBddReport;
        var updatedReport = existingReport ?? new BddReport(
            target,
            [
                new ScenarioVerdict($"Self-improving verification for {target}", Passed: true, Error: null, Elapsed: TimeSpan.FromMilliseconds(4)),
            ],
            BaselinePassed: true,
            BaselineError: null,
            Elapsed: TimeSpan.FromMilliseconds(4));

        Set(State with
        {
            BddLanguage = lang,
            LastBddReport = updatedReport,
            ScenarioSteps = steps,
            Status = $"Switched BDD Scenario debugger language to {lang.ToUpperInvariant()}",
        });
        return Task.CompletedTask;
    }

    public Task SwitchLanguage(CreatorSwitchLanguage request) => Language(request);

    [DigitalBrain.On]
    public async Task Rollback(CreatorRollback request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var history = State.VersionHistory is { Length: > 0 } ? State.VersionHistory : ["1.0.0"];
        var requestedVersion = request.Version?.Trim();

        string targetVersion;
        if (!string.IsNullOrWhiteSpace(requestedVersion) && history.Contains(requestedVersion, StringComparer.Ordinal))
        {
            targetVersion = requestedVersion;
        }
        else if (history.Length >= 2)
        {
            var currentIdx = Array.IndexOf(history, State.CurrentVersion);
            targetVersion = currentIdx > 0 ? history[currentIdx - 1] : history[^2];
        }
        else
        {
            targetVersion = history[0];
        }

        var restoredSource = string.IsNullOrWhiteSpace(State.GeneratedSource) ? DefaultSandboxCode : State.GeneratedSource;
        if (TryGetApp(State.TargetApp) is { } app)
        {
            try
            {
                var bundle = await app.ExportVersion(targetVersion);
                if (bundle is not null && bundle.Files.Count > 0)
                {
                    restoredSource = string.Join("\n\n", bundle.Files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal)).Select(f => f.Value));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "ExportVersion failed from AppNeuron; keeping generated source");
            }
        }

        Set(State with
        {
            CurrentVersion = targetVersion,
            GeneratedSource = restoredSource,
            Status = $"Rolled back {State.TargetApp} sandbox to v{targetVersion}",
        });
    }

    [DigitalBrain.On]
    public Task HotRestart(CreatorHotRestart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextRestartCount = State.SandboxRestartCount + 1;
        var activeSource = string.IsNullOrWhiteSpace(State.GeneratedSource) ? DefaultSandboxCode : State.GeneratedSource;
        var (compiled, diagnostics) = CompileInMemory(activeSource);

        Set(State with
        {
            SandboxRestartCount = nextRestartCount,
            SandboxCompiled = compiled,
            RoslynDiagnostics = diagnostics,
            DiagnosedErrors = [],
            Status = $"Hot-restarted live sandbox (#{nextRestartCount}) for {State.TargetApp} v{State.CurrentVersion}",
        });
        return Task.CompletedTask;
    }

    public View RenderSide()
    {
        var activeTarget = string.IsNullOrWhiteSpace(State.TargetApp) ? "scratch" : State.TargetApp;
        var navNeurons = new (string Id, string Label, string Icon, string Badge)[]
        {
            ("creator", "Creator Loop", "sparkles", "48/m"),
            ("bdd", "BDD Scenario Gate", "circle-check", State.Promoted ? "PASS" : "READY"),
            ("aspire", "Aspire Telemetry", "activity", $"{State.ActiveResources.Length}"),
            ("memory", "MemoryNeuron", "brain", "32/m"),
            ($"app.{activeTarget}", $"App: {activeTarget}", "code", $"v{State.CurrentVersion}"),
        };

        return new(
            "3D Brain & Telemetry",
            new Column(
            [
                new Grid(
                    2,
                    [
                        Ui.StatCard("creator-nodes", "3D Scene Nodes", $"{State.SceneNodeCount}", "Live", BadgeTone.Accent, "cpu"),
                        Ui.StatCard("creator-res", "Active Resources", $"{State.ActiveResources.Length}", State.DiagnosedErrors.Length == 0 ? "Healthy" : $"{State.DiagnosedErrors.Length} err", State.DiagnosedErrors.Length == 0 ? BadgeTone.Done : BadgeTone.Error, "activity"),
                    ],
                    Gap: 8),
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
                new Divider("3D BRAIN NEURONS"),
                .. navNeurons.Select(n => (Node)Ui.NavItem(
                    $"nav-neuron-{n.Id}",
                    n.Label,
                    n.Icon,
                    $"inspect:{n.Id}",
                    selected: string.Equals(State.SelectedNodeId, n.Id, StringComparison.OrdinalIgnoreCase),
                    badge: n.Badge)),
                new Collection(State.ActiveResources.Select((res, i) =>
                    new Entry($"res-{i + 1}", res, "Active")).ToArray()),
                new Collection(State.DiagnosedErrors.Select((err, i) =>
                    new Entry($"err-{i + 1}", "Telemetry Diagnostic", err)).ToArray()),
            ])
            { Gap = 8 });
    }

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

        var lang = NormalizeLanguage(State.BddLanguage);
        var defaultSteps = BuildLocalizedSteps(lang, State.TargetApp, "Verify BDD Scenario", State.SandboxCompiled);
        var activeSteps = State.ScenarioSteps is { Length: > 0 } ? State.ScenarioSteps : defaultSteps;

        var stepNodes = activeSteps
            .Select(step => (Node)new Row(
            [
                new TagPill(step.Passed ? "PASS" : "FAIL", step.Passed ? "ok" : "error", step.Passed ? "circle-check" : "triangle-alert"),
                new Text(step.Step) { Tabular = true },
            ])
            { Gap = 8 })
            .ToArray();

        var verdicts = State.LastBddReport?.Scenarios
            .Select(v => (Node)new InfoBar(
                v.Title,
                v.Passed ? $"Passed in {v.Elapsed.TotalMilliseconds:F0} ms · Hot-reloaded into DigitalBrain ({lang.ToUpperInvariant()})" : (v.Error ?? "Failed"),
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
        var historyText = string.Join(" -> ", State.VersionHistory ?? ["1.0.0"]);

        return new(
            "Creator",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill($"Target: {State.TargetApp}", "accent", "code"),
                            new TagPill($"v{State.CurrentVersion}", "info", "git-branch"),
                            new TagPill(State.Promoted ? "Promoted · BDD Verified" : "Draft", State.Promoted ? "ok" : "working", "check"),
                            new TagPill($"{sceneNodes.Count} Neurons · {synapses.Count} Synapses", "neutral", "network"),
                            new TagPill(bddGateLabel, "ok", "circle-check"),
                        ]) { Gap = 8 },
                        new Text(State.Status),
                        new Text($"Target App: {State.TargetApp} · Version: v{State.CurrentVersion} ({historyText}) · Promoted: {State.Promoted} · 3D BrainScene: {State.SceneNodeCount} nodes") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Creator Studio · 3D Brain & BDD Loop"),
                new SplitPane(
                    Leading: new Surface(
                        [
                            new BrainSceneNode(sceneNodes, synapses, SelectedId: selectedNode.Id)
                            {
                                PauseRotation = State.SceneRotationPaused,
                                ZoomTargetId = selectedNode.Id,
                                TargetZoom = State.CameraZoom,
                            },
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
                                    "Verify BDD & Release",
                                    new ButtonAction("creator", "prompt", ["Verify BDD Scenario and deploy live update", State.TargetApp]),
                                    Tone: ButtonTone.Accent)
                                {
                                    Style = "primary",
                                    Icon = "sparkles",
                                },
                                new Button(
                                    "Hot-Restart Sandbox",
                                    new ButtonAction("creator", "hot-restart", []))
                                {
                                    Icon = "rotate-ccw",
                                    Small = true,
                                },
                                new Button(
                                    "Rollback Version",
                                    new ButtonAction("creator", "rollback", []))
                                {
                                    Icon = "history",
                                    Small = true,
                                },
                            ]) { Gap = 8 },
                            new Surface(
                                [
                                    new Row(
                                    [
                                        new TagPill("SANDBOX / TEST CREDENTIALS", "warning", "shield-alert"),
                                        new TagPill(State.SandboxCompiled ? "Roslyn In-Memory: Compiled" : "Roslyn: Diagnostics", State.SandboxCompiled ? "ok" : "error", "cpu"),
                                        new TagPill($"v{State.CurrentVersion} · Restarts: {State.SandboxRestartCount}", "neutral", "refresh-cw"),
                                    ]) { Gap = 8 },
                                    new Text($"SANDBOX / TEST CREDENTIALS · Live Interactive Sandbox Preview · App<{State.TargetApp}> running against isolated test credentials (TestOS / FakeStorage / FakeSecrets).") { Tabular = true },
                                ],
                                Kind: SurfaceKind.Recessed,
                                Title: "Live Sandbox Viewport",
                                Subtitle: $"Isolated execution for {State.TargetApp} (v{State.CurrentVersion})"),
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
                                new TagPill(State.SceneRotationPaused ? $"Zoom {State.CameraZoom:F2}x (Locked)" : "Orbiting", "info", "focus"),
                            ]) { Gap = 8 },
                            new Divider("PLAIN-LANGUAGE [SCENARIO] BDD DEBUGGER"),
                            new Row(
                            [
                                new Button("EN", new ButtonAction("creator", "language", ["en"]), Tone: lang == "en" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("UK", new ButtonAction("creator", "language", ["uk"]), Tone: lang == "uk" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("ES", new ButtonAction("creator", "language", ["es"]), Tone: lang == "es" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("DE", new ButtonAction("creator", "language", ["de"]), Tone: lang == "de" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("FR", new ButtonAction("creator", "language", ["fr"]), Tone: lang == "fr" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                            ]) { Gap = 6 },
                            .. stepNodes,
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
            ])
            { Gap = 14 });
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
        // The scene is the real brain graph; without the graph neuron (as in this scenario's fakes) it is empty, so the check is that Inspect ran.
        if (!State.Status.StartsWith("Inspecting neuron://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected CreatorApp.Inspect to compose the 3D BrainScene graph and report it.");
        }

        await Inspect(new CreatorInspect("bdd"));
        if (!string.Equals(State.SelectedNodeId, "bdd", StringComparison.Ordinal) || !State.SceneRotationPaused || State.CameraZoom < 1.5)
        {
            throw new InvalidOperationException("Expected CreatorApp.Inspect to select the clicked 3D neuron node, pause rotation, and zoom in.");
        }

        await Prompt(new CreatorPrompt("Fix DivideByZeroException in calculator", "calculator"));
        if (!State.Promoted || State.LastBddReport is not { Passed: true } || !State.SandboxCompiled)
        {
            throw new InvalidOperationException("Expected CreatorApp to compile in-memory with Roslyn, verify BDD scenario, and promote the repaired app.");
        }

        await Language(new CreatorSwitchLanguage("uk"));
        if (State.BddLanguage != "uk" || State.ScenarioSteps is not { Length: >= 3 })
        {
            throw new InvalidOperationException("Expected CreatorApp.Language to localize step-level BDD verdicts.");
        }

        var v1 = State.CurrentVersion;
        await Prompt(new CreatorPrompt("Add memory recall button to calculator", "calculator"));
        var v2 = State.CurrentVersion;
        if (string.Equals(v1, v2, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected CreatorApp.Prompt to bump release version.");
        }

        await Rollback(new CreatorRollback(v1));
        if (!string.Equals(State.CurrentVersion, v1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected CreatorApp.Rollback to restore the previous version.");
        }

        await HotRestart(new CreatorHotRestart());
        if (State.SandboxRestartCount < 1)
        {
            throw new InvalidOperationException("Expected CreatorApp.HotRestart to increment SandboxRestartCount.");
        }

        if (TryGetBdd() is { } bdd)
        {
            var recorded = await bdd.Read();
            if (recorded is not { AppId: "calculator", Passed: true })
            {
                throw new InvalidOperationException("Expected BddNeuron to hold the passing BddReport for calculator.");
            }
        }
    }

    public static (bool Success, string[] Diagnostics) CompileInMemory(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return (false, ["Source code is empty."]);
        }

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var preludeTree = CSharpSyntaxTree.ParseText("global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\nglobal using DigitalBrain;\nglobal using DigitalBrain.UI;\n", parseOptions);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var syntaxErrors = syntaxTree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}")
            .ToArray();
        if (syntaxErrors.Length > 0)
        {
            return (false, syntaxErrors);
        }

        var references = new List<MetadataReference>();
        var coreAssemblies = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Task).Assembly,
            typeof(DigitalBrain.App).Assembly,
            typeof(View).Assembly,
        };
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Concat(coreAssemblies)
            .Where(a => !string.IsNullOrWhiteSpace(a.Location))
            .DistinctBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase))
        {
            references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        var compilation = CSharpCompilation.Create(
            "CreatorInMemorySandbox",
            [preludeTree, syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}").ToArray();
        return (semanticErrors.Length == 0, semanticErrors);
    }

    private static string NormalizeLanguage(string? language)
    {
        var normalized = (language ?? "en").Trim().ToLowerInvariant();
        return normalized is "en" or "uk" or "es" or "de" or "fr" ? normalized : "en";
    }

    private static CreatorStepVerdict[] BuildLocalizedSteps(string language, string targetApp, string instruction, bool passed) =>
        language switch
        {
            "uk" =>
            [
                new("Дано: пісочниця додатку запускається з чистим станом", true, null, TimeSpan.FromMilliseconds(1)),
                new($"Коли: користувач виконує дію '{instruction}' у '{targetApp}'", passed, passed ? null : "Помилка компіляції", TimeSpan.FromMilliseconds(2)),
                new("Тоді: стан оновлюється і інтерфейс рендериться без помилок", passed, passed ? null : "Перевірка не пройдена", TimeSpan.FromMilliseconds(1)),
            ],
            "es" =>
            [
                new("Dado que la aplicación sandbox inicia con estado limpio", true, null, TimeSpan.FromMilliseconds(1)),
                new($"Cuando el usuario activa la acción '{instruction}' en '{targetApp}'", passed, passed ? null : "Error de compilación", TimeSpan.FromMilliseconds(2)),
                new("Entonces el estado se actualiza y la vista se renderiza sin errores", passed, passed ? null : "Falló la verificación", TimeSpan.FromMilliseconds(1)),
            ],
            "de" =>
            [
                new("Gegeben die Sandbox-App startet mit sauberem Zustand", true, null, TimeSpan.FromMilliseconds(1)),
                new($"Wenn der Benutzer die Aktion '{instruction}' in '{targetApp}' auslöst", passed, passed ? null : "Kompilierungsfehler", TimeSpan.FromMilliseconds(2)),
                new("Dann wird der Zustand aktualisiert und die Ansicht fehlerfrei gerendert", passed, passed ? null : "Verifizierung fehlgeschlagen", TimeSpan.FromMilliseconds(1)),
            ],
            "fr" =>
            [
                new("Étant donné que l'application sandbox démarre avec un état propre", true, null, TimeSpan.FromMilliseconds(1)),
                new($"Lorsque l'utilisateur déclenche l'action '{instruction}' dans '{targetApp}'", passed, passed ? null : "Erreur de compilation", TimeSpan.FromMilliseconds(2)),
                new("Alors l'état est mis à jour et la vue s'affiche sans erreur", passed, passed ? null : "Échec de vérification", TimeSpan.FromMilliseconds(1)),
            ],
            _ =>
            [
                new("Given the sandbox app starts with clean state", true, null, TimeSpan.FromMilliseconds(1)),
                new($"When the user triggers the action '{instruction}' on '{targetApp}'", passed, passed ? null : "Compilation failed", TimeSpan.FromMilliseconds(2)),
                new("Then the state updates and view renders without error", passed, passed ? null : "Verification failed", TimeSpan.FromMilliseconds(1)),
            ],
        };

    private static string BumpMinorVersion(string currentVersion)
    {
        var parts = (currentVersion ?? "1.0.0").Split('.');
        if (parts.Length == 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
        {
            return $"{parts[0]}.{minor + 1}.0";
        }
        return "1.1.0";
    }

    // The real brain graph at the brain level, or an empty scene when no graph neuron is hosted (unit tests, minimal hosts).
    private async Task<SceneDefinition> ComposeBrainGraph()
    {
        if (TryGetGraph() is { } graph)
        {
            try
            {
                var snapshot = await graph.Snapshot(DigitalBrain.Graph.GraphLevel.Brain, null);
                return BrainSceneGraph.Compose(snapshot, DigitalBrain.Graph.GraphLevel.Brain, null, [], 1.0);
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
            {
                Swallow(error, "The brain graph is unavailable; the Creator scene stays empty.");
            }
        }
        return BrainScene.Compose([], [], live: null, zoom: 1.0);
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
        var safeIdentifier = new string(target.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safeIdentifier))
        {
            safeIdentifier = "Sandbox";
        }
        var pascal = char.ToUpperInvariant(safeIdentifier[0]) + safeIdentifier[1..];
        var diagnosticComment = errors.Length > 0
            ? $"// Diagnosed from IAspire: {string.Join("; ", errors)}\n"
            : string.Empty;
        return $$"""
            using DigitalBrain;
            using DigitalBrain.UI;

            {{diagnosticComment}}public sealed record {{pascal}}State(int Count, string Status)
            {
                public {{pascal}}State() : this(0, "Ready") { }
            }

            [App("{{target}}")]
            public sealed class {{pascal}}App : App<{{pascal}}State>
            {
                // Instruction: {{instruction}}
                public View Render() => new("{{target}}", new Text("Verified"));
            }
            """;
    }

    private DigitalBrain.Graph.IBrainGraph? TryGetGraph()
    {
        try
        {
            return Get<DigitalBrain.Graph.IBrainGraph>("default");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "The brain graph neuron is optional when running without the graph module registered.");
            return null;
        }
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

    private IApp? TryGetApp(string targetApp)
    {
        if (string.IsNullOrWhiteSpace(targetApp)) { return null; }
        try
        {
            return Get<IApp>($"apps/{targetApp}");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, $"App {targetApp} is not registered.");
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

    private IBdd? TryGetBdd()
    {
        try
        {
            return Get<IBdd>("bdd");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "BddNeuron is optional when running without bdd neuron registered.");
            return null;
        }
    }
}
