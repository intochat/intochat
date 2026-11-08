using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Creator;

internal static class CreatorRendering
{
    public static View RenderSide(CreatorAppState state)
    {
        var activeTarget = string.IsNullOrWhiteSpace(state.TargetApp) ? "scratch" : state.TargetApp;
        var navNeurons = new (string Id, string Label, string Icon, string Badge)[]
        {
            ("creator", "Creator Loop", "sparkles", "48/m"),
            ("bdd", "BDD Scenario Gate", "circle-check", state.Promoted ? "PASS" : "READY"),
            ("aspire", "Aspire Telemetry", "activity", $"{state.ActiveResources.Length}"),
            ("memory", "MemoryNeuron", "brain", "32/m"),
            ($"app.{activeTarget}", $"App: {activeTarget}", "code", $"v{state.CurrentVersion}"),
        };

        return new(
            "3D Brain & Telemetry",
            new Column(
            [
                new Grid(
                    2,
                    [
                        Ui.StatCard("creator-nodes", "3D Scene Nodes", $"{state.SceneNodeCount}", "Live", BadgeTone.Accent, "cpu"),
                        Ui.StatCard("creator-res", "Active Resources", $"{state.ActiveResources.Length}", state.DiagnosedErrors.Length == 0 ? "Healthy" : $"{state.DiagnosedErrors.Length} err", state.DiagnosedErrors.Length == 0 ? BadgeTone.Done : BadgeTone.Error, "activity"),
                    ],
                    Gap: 8),
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill($"3D Scene Nodes: {state.SceneNodeCount}", "accent", "cpu"),
                            new TagPill($"{state.ActiveResources.Length} resource(s)", "ok", "activity"),
                        ]) { Gap = 6 },
                        new Text($"3D Scene Nodes: {state.SceneNodeCount}") { Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Aspire Telemetry"),
                new Divider("3D BRAIN NEURONS"),
                .. navNeurons.Select(n => (Node)Ui.NavItem(
                    $"nav-neuron-{n.Id}",
                    n.Label,
                    n.Icon,
                    $"inspect:{n.Id}",
                    selected: string.Equals(state.SelectedNodeId, n.Id, StringComparison.OrdinalIgnoreCase),
                    badge: n.Badge)),
                new Collection(state.ActiveResources.Select((res, i) =>
                    new Entry($"res-{i + 1}", res, "Active")).ToArray()),
                new Collection(state.DiagnosedErrors.Select((err, i) =>
                    new Entry($"err-{i + 1}", "Telemetry Diagnostic", err)).ToArray()),
            ])
            { Gap = 8 });
    }

    public static View Render(CreatorAppState state)
    {
        var sceneNodes = new List<BrainGraphNode>
        {
            new("creator", "Creator Loop", "neuron://local/creator", "IntoChat.OS.Studio", 0, 0, 0, "working", "Self-Improving Studio", new Button("inspect-creator", "Inspect", Emit: AppSignals.Emit("creator", "inspect", "creator"))),
            new("bdd", "BDD Scenario Gate", "neuron://local/bdd", "DigitalBrain.Bdd", -90, 45, -20, state.Promoted ? "done" : "idle", "Executable [Scenario] Gate", new Button("inspect-bdd", "Inspect", Emit: AppSignals.Emit("creator", "inspect", "bdd"))),
            new("aspire", "Aspire Telemetry", "neuron://local/aspire", "Microsoft.Aspire", 95, 40, -15, state.DiagnosedErrors.Length > 0 ? "error" : "working", "OpenTelemetry Logs & Traces", new Button("inspect-aspire", "Inspect", Emit: AppSignals.Emit("creator", "inspect", "aspire"))),
            new("memory", "MemoryNeuron", "neuron://local/memory", "IntoChat.OS.AI.Memory", -70, -55, 25, "idle", "Episodic & Semantic Recall", new Button("inspect-memory", "Inspect", Emit: AppSignals.Emit("creator", "inspect", "memory"))),
        };
        var synapses = new List<BrainGraphSynapse>
        {
            new("creator", "bdd", "bdd.verified", Active: true),
            new("creator", "aspire", "aspire.telemetry.changed", Active: true),
            new("creator", "memory", "memory.recall", Active: false),
        };
        if (!string.IsNullOrWhiteSpace(state.TargetApp))
        {
            var appKey = $"app.{state.TargetApp}";
            sceneNodes.Add(new(
                appKey,
                $"App: {state.TargetApp}",
                $"neuron://local/apps/{state.TargetApp}",
                "IntoChat.OS.Sandbox",
                75,
                -50,
                30,
                state.Promoted ? "done" : "working",
                "C# Sandbox App<TState>",
                new Button($"inspect-{appKey}", "Inspect", Emit: AppSignals.Emit("creator", "inspect", appKey))));
            synapses.Add(new("creator", appKey, "app.promoted", Active: state.Promoted));
        }

        var selectedNode = sceneNodes.FirstOrDefault(n =>
            string.Equals(n.Id, state.SelectedNodeId, StringComparison.OrdinalIgnoreCase)) ?? sceneNodes[0];
        var signalsPerMin = selectedNode.Id switch
        {
            "creator" => 48,
            "aspire" => 96,
            "memory" => 32,
            "bdd" => 24,
            _ => 18,
        };
        var scenarioCount = state.LastBddReport?.Scenarios.Count ?? 4;
        var bddGateLabel = state.Promoted
            ? $"BDD Gate: {scenarioCount}/{scenarioCount} Passed"
            : $"BDD Gate: {scenarioCount}/{scenarioCount} Ready";

        var lang = CreatorSession.NormalizeLanguage(state.BddLanguage);
        var defaultSteps = CreatorSession.BuildLocalizedSteps(lang, state.TargetApp, "Verify BDD Scenario", state.SandboxCompiled);
        var activeSteps = state.ScenarioSteps is { Length: > 0 } ? state.ScenarioSteps : defaultSteps;

        var stepNodes = activeSteps
            .Select(step => (Node)new Row(
            [
                new TagPill(step.Passed ? "PASS" : "FAIL", step.Passed ? "ok" : "error", step.Passed ? "circle-check" : "triangle-alert"),
                new Text(step.Step) { Tabular = true },
            ])
            { Gap = 8 })
            .ToArray();

        var verdicts = state.LastBddReport?.Scenarios
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

        var activeCode = (string.IsNullOrWhiteSpace(state.GeneratedSource)
            ? CreatorSession.DefaultSandboxCode
            : state.GeneratedSource).Replace("\r\n", "\n", StringComparison.Ordinal);
        var historyText = string.Join(" -> ", state.VersionHistory ?? ["1.0.0"]);

        return new(
            "Creator",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill($"Target: {state.TargetApp}", "accent", "code"),
                            new TagPill($"v{state.CurrentVersion}", "info", "git-branch"),
                            new TagPill(state.Promoted ? "Promoted · BDD Verified" : "Draft", state.Promoted ? "ok" : "working", "check"),
                            new TagPill($"{sceneNodes.Count} Neurons · {synapses.Count} Synapses", "neutral", "network"),
                            new TagPill(bddGateLabel, "ok", "circle-check"),
                        ]) { Gap = 8 },
                        new Text(state.Status),
                        new Text($"Target App: {state.TargetApp} · Version: v{state.CurrentVersion} ({historyText}) · Promoted: {state.Promoted} · 3D BrainScene: {state.SceneNodeCount} nodes") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Creator Studio · 3D Brain & BDD Loop"),
                new SplitPane(
                    Leading: new Surface(
                        [
                            new BrainSceneNode(sceneNodes, synapses, SelectedId: selectedNode.Id)
                            {
                                PauseRotation = state.SceneRotationPaused,
                                ZoomTargetId = selectedNode.Id,
                                TargetZoom = state.CameraZoom,
                            },
                            .. state.DiagnosedErrors.Select(err => (Node)new InfoBar("Aspire Diagnostic", err, "error")),
                            new Row(
                            [
                                new Field(
                                    "prompt",
                                    "prompt",
                                    FieldKind.Text,
                                    "Prompt Creator to synthesize or self-improve a C# App<TState>…",
                                    AppSignals.Emit("creator", "prompt", "Self-improve C# App<TState>", state.TargetApp)),
                                new Button(
                                    "verify-bdd-release",
                                    "Verify BDD & Release",
                                    Emit: AppSignals.Emit("creator", "prompt", "Verify BDD Scenario and deploy live update", state.TargetApp),
                                    Tone: ButtonTone.Accent)
                                {
                                    Style = "primary",
                                    Icon = "sparkles",
                                },
                                new Button(
                                    "hot-restart-sandbox",
                                    "Hot-Restart Sandbox",
                                    Emit: AppSignals.Emit("creator", "hot-restart"))
                                {
                                    Icon = "rotate-ccw",
                                    Small = true,
                                },
                                new Button(
                                    "rollback-version",
                                    "Rollback Version",
                                    Emit: AppSignals.Emit("creator", "rollback"))
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
                                        new TagPill(state.SandboxCompiled ? "Roslyn In-Memory: Compiled" : "Roslyn: Diagnostics", state.SandboxCompiled ? "ok" : "error", "cpu"),
                                        new TagPill($"v{state.CurrentVersion} · Restarts: {state.SandboxRestartCount}", "neutral", "refresh-cw"),
                                    ]) { Gap = 8 },
                                    new Text($"SANDBOX / TEST CREDENTIALS · Live Interactive Sandbox Preview · App<{state.TargetApp}> running against isolated test credentials (TestOS / FakeStorage / FakeSecrets).") { Tabular = true },
                                ],
                                Kind: SurfaceKind.Recessed,
                                Title: "Live Sandbox Viewport",
                                Subtitle: $"Isolated execution for {state.TargetApp} (v{state.CurrentVersion})"),
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
                                    "copy-uri",
                                    "Copy URI",
                                    Intent: AppSignals.Copy(selectedNode.Uri))
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
                                new TagPill(state.SceneRotationPaused ? $"Zoom {state.CameraZoom:F2}x (Locked)" : "Orbiting", "info", "focus"),
                            ]) { Gap = 8 },
                            new Divider("PLAIN-LANGUAGE [SCENARIO] BDD DEBUGGER"),
                            new Row(
                            [
                                new Button("en", "EN", Emit: AppSignals.Emit("creator", "language", "en"), Tone: lang == "en" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("uk", "UK", Emit: AppSignals.Emit("creator", "language", "uk"), Tone: lang == "uk" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("es", "ES", Emit: AppSignals.Emit("creator", "language", "es"), Tone: lang == "es" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("de", "DE", Emit: AppSignals.Emit("creator", "language", "de"), Tone: lang == "de" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                                new Button("fr", "FR", Emit: AppSignals.Emit("creator", "language", "fr"), Tone: lang == "fr" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
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
                        Subtitle: $"Synthesized C# App<{state.TargetApp}> & [Scenario] Verdicts"),
                    LeadingExtent: 460,
                    Gap: 16),
            ])
            { Gap = 14 });
    }
}
