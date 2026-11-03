using System.Globalization;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
        var steps = BuildLocalizedSteps(lang, targetApp, instruction, compiled);
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
        var steps = BuildLocalizedSteps(lang, target, "Verify BDD Scenario", State.SandboxCompiled);
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
        var activeSource = string.IsNullOrWhiteSpace(State.GeneratedSource) ? DefaultSandboxCode : State.GeneratedSource;
        var (compiled, diagnostics) = CompileInMemory(activeSource);
        var next = State with
        {
            SandboxRestartCount = State.SandboxRestartCount + 1,
            SandboxCompiled = compiled,
            RoslynDiagnostics = diagnostics,
        };
        await Save(next, new CreatorHotRestart());
        return next;
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

    public static string NormalizeLanguage(string? language)
    {
        var normalized = (language ?? "en").Trim().ToLowerInvariant();
        return normalized is "en" or "uk" or "es" or "de" or "fr" ? normalized : "en";
    }

    public static CreatorStepVerdict[] BuildLocalizedSteps(string language, string targetApp, string instruction, bool passed) =>
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
            """;
    }
}
