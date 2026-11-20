using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Testing.Synapses;
using DigitalBrain.Units;
using global::Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.Apps;

// The Builder's two tools: read_contracts (what a slot can be and what a view is made of) and check_unit (compile as a deploy
// will, then run the authored scenarios on fake slots). Nothing here reflects; the contract text is the framework's surface.
internal static partial class BuilderTools
{
    public const string ReadContracts = "read_contracts";
    public const string CheckUnit = "check_unit";

    public static int MaxSidecarChars => SynapseCompiler.MaxSidecarChars;

    private static readonly Lazy<SynapseCompiler> DefaultCompiler = new(() => new SynapseCompiler(ReferenceSet.FromDefaultContext()));

    public static Task<UnitCheck> VerifyPackageAsync(
        IServiceProvider? services,
        string alias,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyList<Scenario> scenarios,
        CancellationToken ct = default)
    {
        var compiler = services?.GetService<SynapseCompiler>() ?? DefaultCompiler.Value;
        return Check(compiler, alias, files, needs: null, scenarios, ct);
    }

    public static readonly IReadOnlyList<InferenceTool> Definitions =
    [
        new(ReadContracts, "Read the capability contracts a [Slot] parameter can be (IHttpRead, ITool, IStorage) and the DigitalBrain.UI nodes a view is built from.",
            """{"type":"object","properties":{}}"""),
        new(CheckUnit, "Compile the unit's C# files (path to full source, for example XWatch.cs) as a deploy will, with the synapse analyzers and the host allow-list, then run every authored scenario against them on fake slots. Returns passed, diagnostics and scenario failures; both are errors.",
            """{"type":"object","properties":{"files":{"type":"object","additionalProperties":{"type":"string"}}},"required":["files"]}"""),
    ];

    internal sealed record CheckArguments(IReadOnlyDictionary<string, string>? Files);

    // What check_unit found: compile diagnostics and scenario failures, one line each, together.
    internal sealed record UnitCheck(bool Passed, IReadOnlyList<ScriptCompilationDiagnostic> Diagnostics, IReadOnlyList<string> Failures);

    public const string Contracts = """
        namespace DigitalBrain.Capabilities.Http;   // capability "http": GET against the slot's allowed hosts only
        public interface IHttpRead
        {
            Task<JsonElement> GetJson(string url, CancellationToken ct);   // parses the body as JSON
            Task<string> GetText(string url, CancellationToken ct);
        }   // another host throws HostNotAllowedException

        namespace DigitalBrain.Capabilities.Tools;  // capability "tool": the connector tools the person has
        public interface ITool
        {
            Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct);
            Task<IReadOnlyList<string>> List();
        }

        namespace DigitalBrain.Capabilities.Storage; // capability "storage": structured and unstructured file storage
        public interface IStorage
        {
            Task<StorageDescriptor> Describe(CancellationToken ct);
            Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct);
            Task<OperationId> Write(WritePlan plan, CancellationToken ct);
        }

        namespace IntoChat.OS.Modules.Apps.Notifications; // capability "notifications": brain notification inbox
        public interface INotifications
        {
            Task<JsonElement> Notify(string title, string body, string priority, CancellationToken ct);
        }

        namespace DigitalBrain.UI;  // a view synapse returns new View(title, root)
        public sealed record View(string Title, Node Root);
        public sealed record Surface(IReadOnlyList<Node> Children, string Kind = "acrylic", string? Title = null, string? Subtitle = null, double Padding = 18) : Node;
        public sealed record Column(IReadOnlyList<Node> Children) : Node;
        public sealed record Row(IReadOnlyList<Node> Children) : Node;
        public sealed record Text(string Value) : Node;
        public sealed record Badge(string Label, string Tone = "neutral", string? Icon = null) : Node;   // tone: neutral, accent, success, warning, danger
        public sealed record StatCardNode(string Key, string Label, string Value, string? Delta = null) : Node;
        public sealed record Field(string Key, string Name, FieldKind Kind, string? Placeholder = null, Command? OnSubmit = null, string AutomationName = "") : Node;
        public sealed record Button(string Key, string Label, Command? Command = null, Intent? Intent = null, bool Enabled = true, string Tone = "neutral", string AutomationName = "", string? Tooltip = null) : Node;   // Key is stable within the view; give Command
        public sealed record Command(string Target, string Signal, JsonElement? Args = null);                // Target "." = the unit itself

        namespace DigitalBrain;
        [State("alias")] record; [Signal("signal.alias")] record : Signal; [Slot] parameter;
        [Synapse(typeof(Tick), "alias", Every = "every 6m")]  [Synapse(typeof(TSignal), "alias")]  [Synapse("signal.alias", "alias")] (SignalEnvelope)  [Synapse("alias")] (view)
        // Synchronous Asks<T> pattern (never use C# anonymous types `new { ... }`; use Dictionary<string, string> or records):
        // [Synapse(typeof(Tick), "alias", Every = "every 1m")] static Reaction<TState> Poll(Tick tick, TState state, [Slot] Asks<IHttpRead> http) => Reaction<TState>.Of(state).Ask(http.GetJson(url, key: "spot"));
        // [Synapse(typeof(Answered), "alias")] static Reaction<TState> Heard(Answered all, Answer answer, TState state) => ...
        Reaction<TState>.Of(TState state, params Signal[] said)
        """;

    // Compile stage: run SynapseCompiler and closed-world PE boundary check on unit files
    public static (bool Success, IReadOnlyList<ScriptCompilationDiagnostic> Diagnostics) CompileStage(SynapseCompiler compiler, SynapseSource source)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(source);
        var build = compiler.Compile(source);
        var diagnostics = build.Diagnostics.Select(d => new ScriptCompilationDiagnostic(d.File, d.Id, d.Line, d.Message)).ToList();
        if (build.Success && SynapseCompiler.Verify(build) is { } boundaryError)
        {
            diagnostics.Add(new ScriptCompilationDiagnostic("", "DB0099", 0, boundaryError));
            return (false, diagnostics);
        }

        return (build.Success, diagnostics);
    }

    // Verify stage: run scenarios on fake slots and check schedule
    public static async Task<IReadOnlyList<string>> VerifyStage(SynapseSource source, UnitNeeds? needs, IReadOnlyList<Scenario> scenarios, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scenarios);
        var failures = new List<string>();
        if (needs is not null && ScheduleMismatch(needs.Schedule, source.Files) is { } mismatch) { failures.Add(mismatch); }
        foreach (var scenario in KeptScenarios.Live(scenarios))
        {
            ScenarioSteps.Parsed parsed;
            try { parsed = ScenarioSteps.Parse(scenario); }
            catch (FormatException error) { failures.Add($"{scenario.Name}: {error.Message}"); continue; }
            var transcript = await UnitScenario.Run(source, parsed.Fakes, parsed.Steps, ct).ConfigureAwait(false);
            failures.AddRange(transcript.Failures.Select(failure => $"{scenario.Name}: {failure}"));
        }
        return failures;
    }

    // The check a deploy makes, plus the scenarios: a build that does not compile runs no scenario.
    // needs is the authored needs, or null when there are none to hold the code to (a build flow draft).
    public static async Task<UnitCheck> Check(SynapseCompiler compiler, string alias, IReadOnlyDictionary<string, string> files,
        UnitNeeds? needs, IReadOnlyList<Scenario> scenarios, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(scenarios);
        var code = UnitFiles.Code(files);
        if (code.Count == 0) { return new UnitCheck(false, [], ["no .cs files"]); }
        var source = new SynapseSource(alias, code, null);
        var (compiled, diagnostics) = CompileStage(compiler, source);
        if (!compiled) { return new UnitCheck(false, diagnostics, []); }
        var failures = await VerifyStage(source, needs, scenarios, ct).ConfigureAwait(false);
        return new UnitCheck(failures.Count == 0, diagnostics, failures);
    }

    // A syntax pass over the compiled code: the Every of each [Synapse(typeof(Tick), ...)] must be the authored schedule, and a
    // schedule needs a Tick synapse.
    internal static string? ScheduleMismatch(string? schedule, IReadOnlyDictionary<string, string> code)
    {
        var found = code.Values.SelectMany(source => TickSynapse().Matches(source))
            .Select(match => EveryValue().Match(match.Value) is { Success: true } every ? every.Groups[1].Value : null).ToArray();
        var wanted = string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim();
        var says = found.Length == 0 ? "no schedule" : string.Join(", ", found.Select(every => every is null ? "a Tick synapse with no Every" : $"'{every}'"));
        return (wanted, found) switch
        {
            (null, { Length: 0 }) => null,
            ({ } w, { Length: > 0 }) when found.All(every => every == w) => null,
            _ => $"needs.schedule is {(wanted is null ? "none" : $"'{wanted}'")} but the code says {says}",
        };
    }

    [GeneratedRegex(@"\[\s*Synapse\s*\(\s*typeof\s*\(\s*(?:DigitalBrain\.Time\.)?Tick\s*\)[^\]]*\]")]
    private static partial Regex TickSynapse();

    [GeneratedRegex(@"Every\s*=\s*""([^""]*)""")]
    private static partial Regex EveryValue();
}

// A unit's source files: its C#, plus spec.md and scenarios.json that travel with it but do not compile.
internal static class UnitFiles
{
    public const string Spec = "spec.md";
    public const string Scenarios = "scenarios.json";

    public static IReadOnlyDictionary<string, string> Code(IReadOnlyDictionary<string, string> files)
        => files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
}
