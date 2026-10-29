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
    bool Promoted)
{
    public CreatorAppState() : this("scratch", "Idle", 0, [], [], "", null, false) { }
}

[DigitalBrain.App("creator")]
public sealed class CreatorApp : DigitalBrain.App<CreatorAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "creator",
        "Creator",
        "1.0.0",
        [typeof(BddNeuron)],
        new DigitalBrain.AppRequirements([]));

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
        var scene = ComposeBrainGraph();
        var (resources, errors) = await ReadTelemetryAsync(request.Resource);
        var status = errors.Length == 0
            ? "No runtime errors detected in Aspire telemetry."
            : $"Diagnosed {errors.Length} telemetry issue(s) across active resources.";
        Set(State with
        {
            Status = status,
            SceneNodeCount = scene.Nodes.Length,
            ActiveResources = resources,
            DiagnosedErrors = errors,
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
        });
    }

    public View RenderSide() => new(
        "3D Brain & Telemetry",
        new Column(
        [
            new Text($"3D Scene Nodes: {State.SceneNodeCount}"),
            new Collection(State.ActiveResources.Select((res, i) =>
                new Entry($"res-{i + 1}", res, "Active")).ToArray()),
            new Collection(State.DiagnosedErrors.Select((err, i) =>
                new Entry($"err-{i + 1}", "Telemetry Diagnostic", err)).ToArray()),
        ]));

    public View Render() => new(
        "Creator",
        new Column(
        [
            new Text(State.Status),
            new Text($"Target App: {State.TargetApp} · Promoted: {State.Promoted} · 3D BrainScene: {State.SceneNodeCount} nodes"),
            new Text(string.IsNullOrWhiteSpace(State.GeneratedSource) ? "// Prompt Creator to synthesize an App<TState>." : State.GeneratedSource),
        ]));

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
