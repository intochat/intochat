using System.Globalization;
using DigitalBrain;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Creator;

[GenerateSerializer, Alias("os.creator.step-verdict")]
public sealed record CreatorStepVerdict(
    [property: Id(0)] string Step,
    [property: Id(1)] bool Passed,
    [property: Id(2)] string? Error = null,
    [property: Id(3)] TimeSpan Elapsed = default);

[GenerateSerializer, Alias("os.creator.session")]
public sealed record CreatorSessionState(
    [property: Id(0)] string TargetApp,
    [property: Id(1)] string CurrentVersion,
    [property: Id(2)] string[] VersionHistory,
    [property: Id(3)] string GeneratedSource,
    [property: Id(4)] bool SandboxCompiled,
    [property: Id(5)] string[] RoslynDiagnostics,
    [property: Id(6)] int SandboxRestartCount,
    [property: Id(7)] string BddLanguage,
    [property: Id(8)] CreatorStepVerdict[] ScenarioSteps)
{
    public CreatorSessionState() : this("scratch", "1.0.0", ["1.0.0"], "", true, [], 0, "en", []) { }
}

[DigitalBrain.Contracts.NeuronAlias("os.creator.session"), DigitalBrain.Contracts.DefaultNeuronType("os.creator.session")]
public interface ICreatorSession
{
    [DigitalBrain.ReadOnlyCall]
    Task<CreatorSessionState> Read();

    Task<CreatorSessionState> RecordPrompt(string targetApp, string instruction, string source, bool compiled, string[] diagnostics, string? language);

    Task<CreatorSessionState> SwitchLanguage(string? language);

    Task<CreatorSessionState> Rollback(string? version, string? restoredSource);

    Task<CreatorSessionState> HotRestart();
}

[DigitalBrain.Neuron("os.creator.session")]
public sealed class CreatorSession : DigitalBrain.Neuron<CreatorSessionState>, ICreatorSession
{
    public const string DefaultSandboxCode = """
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

    public Task<CreatorSessionState> Read() => Task.FromResult(State);

    public async Task<CreatorSessionState> RecordPrompt(
        string targetApp,
        string instruction,
        string source,
        bool compiled,
        string[] diagnostics,
        string? language)
    {
        var lang = NormalizeLanguage(language ?? State.BddLanguage);
        var steps = BuildActivitySteps([], lang, targetApp, instruction, compiled, diagnostics);
        var nextVersion = compiled ? BumpMinorVersion(State.CurrentVersion) : State.CurrentVersion;
        var historyList = State.VersionHistory is { Length: > 0 } ? State.VersionHistory.ToList() : ["1.0.0"];
        if (compiled && !historyList.Contains(nextVersion, StringComparer.Ordinal))
        {
            historyList.Add(nextVersion);
        }
        var history = historyList.Distinct(StringComparer.Ordinal).ToArray();
        var next = State with
        {
            TargetApp = targetApp,
            CurrentVersion = nextVersion,
            VersionHistory = history,
            GeneratedSource = source,
            SandboxCompiled = compiled,
            RoslynDiagnostics = diagnostics,
            BddLanguage = lang,
            ScenarioSteps = steps,
        };
        await Save(next, new CreatorPrompt(instruction, targetApp, lang));
        return next;
    }

    public async Task<CreatorSessionState> SwitchLanguage(string? language)
    {
        var lang = NormalizeLanguage(language);
        var target = string.IsNullOrWhiteSpace(State.TargetApp) ? "scratch" : State.TargetApp;
        var steps = BuildActivitySteps(
            State.ScenarioSteps.Select(s => s.Step).ToArray(),
            lang,
            target,
            "Verify BDD Scenario",
            State.SandboxCompiled,
            State.RoslynDiagnostics);
        var next = State with
        {
            BddLanguage = lang,
            ScenarioSteps = steps,
        };
        await Save(next, new CreatorSwitchLanguage(lang));
        return next;
    }

    public async Task<CreatorSessionState> Rollback(string? version, string? restoredSource)
    {
        var history = State.VersionHistory is { Length: > 0 } ? State.VersionHistory : ["1.0.0"];
        var requestedVersion = version?.Trim();

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

        var next = State with
        {
            CurrentVersion = targetVersion,
            GeneratedSource = string.IsNullOrWhiteSpace(restoredSource)
                ? (string.IsNullOrWhiteSpace(State.GeneratedSource) ? DefaultSandboxCode : State.GeneratedSource)
                : restoredSource,
        };
        await Save(next, new CreatorRollback(targetVersion));
        return next;
    }

    public async Task<CreatorSessionState> HotRestart()
    {
        // The session no longer compiles offline; units are checked by the Creator's check_unit.
        var next = State with { SandboxRestartCount = State.SandboxRestartCount + 1 };
        await Save(next, new CreatorHotRestart());
        return next;
    }

    public static string NormalizeLanguage(string? language)
    {
        var normalized = (language ?? "en").Trim().ToLowerInvariant();
        return normalized is "en" or "uk" or "es" or "de" or "fr" ? normalized : "en";
    }

    public static CreatorStepVerdict[] BuildLocalizedSteps(string language, string targetApp, string instruction, bool passed)
        => BuildActivitySteps([], language, targetApp, instruction, passed);

    public static CreatorStepVerdict[] BuildActivitySteps(
        string[]? activity,
        string language,
        string targetApp,
        string instruction,
        bool passed,
        string[]? diagnostics = null,
        TimeSpan elapsed = default)
    {
        var errorText = passed ? null : (diagnostics is { Length: > 0 } ? string.Join("; ", diagnostics) : "Compilation failed");
        if (activity is { Length: > 0 })
        {
            var perStep = elapsed > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Max(1, elapsed.Ticks / activity.Length)) : TimeSpan.Zero;
            var verdicts = activity
                .Select((entry, idx) => new CreatorStepVerdict(
                    $"[{language.ToUpperInvariant()}] {entry}",
                    passed || idx < activity.Length - 1,
                    !passed && idx == activity.Length - 1 ? errorText : null,
                    perStep))
                .ToList();
            while (verdicts.Count < 3)
            {
                verdicts.Add(new CreatorStepVerdict($"[{language.ToUpperInvariant()}] verified {targetApp} round {verdicts.Count + 1}", passed, errorText, perStep));
            }
            return [.. verdicts];
        }

        var stepElapsed = elapsed > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Max(1, elapsed.Ticks / 3)) : TimeSpan.Zero;
        return
        [
            new($"[{language.ToUpperInvariant()}] build started: {instruction} ({targetApp})", true, null, stepElapsed),
            new($"[{language.ToUpperInvariant()}] round 1: check_csharp ({targetApp})", passed, errorText, stepElapsed),
            new($"[{language.ToUpperInvariant()}] {(passed ? $"Built {targetApp}" : $"build failed: {errorText}")}", passed, errorText, stepElapsed),
        ];
    }

    public static string BumpMinorVersion(string currentVersion)
    {
        var parts = (currentVersion ?? "1.0.0").Split('.');
        if (parts.Length == 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
        {
            return $"{parts[0]}.{minor + 1}.0";
        }
        return "1.1.0";
    }

    public static string SynthesizeAppSource(string target, string instruction, string[] errors)
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
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
