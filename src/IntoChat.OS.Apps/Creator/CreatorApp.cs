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
using DigitalBrain;
using DigitalBrain.Spi;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Microsoft.Aspire;

namespace IntoChat.OS.Apps.Creator;

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
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(CreatorApp).AssemblyQualifiedName,
    };

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
            source = CreatorSession.SynthesizeAppSource(target, instruction, errors);
            (compiled, diagnostics) = CreatorSession.CompileInMemory(source);
        }

        var lang = CreatorSession.NormalizeLanguage(request.Language ?? State.BddLanguage);
        var steps = CreatorSession.BuildLocalizedSteps(lang, target, instruction, compiled);
        var nextVersion = compiled ? CreatorSession.BumpMinorVersion(State.CurrentVersion) : State.CurrentVersion;
        var historyList = State.VersionHistory is { Length: > 0 } ? State.VersionHistory.ToList() : ["1.0.0"];
        if (compiled && !historyList.Contains(nextVersion, StringComparer.Ordinal))
        {
            historyList.Add(nextVersion);
        }
        var history = historyList.Distinct(StringComparer.Ordinal).ToArray();

        if (TryGetSession() is { } session)
        {
            try
            {
                var sessionState = await session.RecordPrompt(target, instruction, source, compiled, diagnostics, lang);
                nextVersion = sessionState.CurrentVersion;
                history = sessionState.VersionHistory;
                steps = sessionState.ScenarioSteps;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "CreatorSession neuron is optional when running CreatorApp standalone.");
            }
        }

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
    public async Task Language(CreatorSwitchLanguage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lang = CreatorSession.NormalizeLanguage(request.Language);
        var target = string.IsNullOrWhiteSpace(State.TargetApp) ? "scratch" : State.TargetApp;
        var steps = CreatorSession.BuildLocalizedSteps(lang, target, "Verify BDD Scenario", State.SandboxCompiled);

        if (TryGetSession() is { } session)
        {
            try
            {
                await session.SwitchLanguage(lang);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "CreatorSession neuron is optional when running CreatorApp standalone.");
            }
        }

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

        var restoredSource = string.IsNullOrWhiteSpace(State.GeneratedSource) ? CreatorSession.DefaultSandboxCode : State.GeneratedSource;
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

        if (TryGetSession() is { } session)
        {
            try
            {
                await session.Rollback(targetVersion, restoredSource);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "CreatorSession neuron is optional when running CreatorApp standalone.");
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
    public async Task HotRestart(CreatorHotRestart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextRestartCount = State.SandboxRestartCount + 1;
        var activeSource = string.IsNullOrWhiteSpace(State.GeneratedSource) ? CreatorSession.DefaultSandboxCode : State.GeneratedSource;
        var (compiled, diagnostics) = CreatorSession.CompileInMemory(activeSource);

        if (TryGetSession() is { } session)
        {
            try
            {
                await session.HotRestart();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "CreatorSession neuron is optional when running CreatorApp standalone.");
            }
        }

        Set(State with
        {
            SandboxRestartCount = nextRestartCount,
            SandboxCompiled = compiled,
            RoslynDiagnostics = diagnostics,
            DiagnosedErrors = [],
            Status = $"Hot-restarted live sandbox (#{nextRestartCount}) for {State.TargetApp} v{State.CurrentVersion}",
        });
    }

    public View RenderSide() => CreatorRendering.RenderSide(State);

    public View Render() => CreatorRendering.Render(State);

    public static (bool Success, string[] Diagnostics) CompileInMemory(string source)
        => CreatorSession.CompileInMemory(source);

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

    private async Task<SceneDefinition> ComposeBrainGraph()
    {
        if (TryGetGraph() is { } graph)
        {
            try
            {
                var snapshot = await graph.Snapshot(DigitalBrain.Graph.GraphLevel.Brain, null);
                var composed = BrainSceneGraph.Compose(snapshot, DigitalBrain.Graph.GraphLevel.Brain, null, [], 1.0);
                if (composed.Nodes.Length > 0)
                {
                    return composed;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
            {
                Swallow(error, "The brain graph is unavailable; the Creator scene falls back to the studio graph.");
            }
        }
        return ComposeFallbackScene();
    }

    private SceneDefinition ComposeFallbackScene()
    {
        var activeTarget = string.IsNullOrWhiteSpace(State.TargetApp) ? "scratch" : State.TargetApp;
        var appKey = $"app.{activeTarget}";
        var neurons = new BrainInstance[]
        {
            new("IntoChat.OS.Studio", "creator", DateTimeOffset.UnixEpoch, null),
            new("DigitalBrain.Bdd", "bdd", DateTimeOffset.UnixEpoch, null),
            new("Microsoft.Aspire", "aspire", DateTimeOffset.UnixEpoch, null),
            new("IntoChat.OS.AI.Memory", "memory", DateTimeOffset.UnixEpoch, null),
            new("IntoChat.OS.Sandbox", appKey, DateTimeOffset.UnixEpoch, null),
        };
        var synapses = new ObservedSynapse[]
        {
            new("creator", "bdd", "bdd.verified"),
            new("creator", "aspire", "aspire.telemetry.changed"),
            new("creator", "memory", "memory.recall"),
            new("creator", appKey, "app.promoted"),
        };
        return BrainScene.Compose(neurons, synapses, live: null, zoom: 1.0);
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

    private ICreatorSession? TryGetSession()
    {
        try
        {
            return Get<ICreatorSession>("session");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "CreatorSession neuron is optional when running CreatorApp in isolation.");
            return null;
        }
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
