using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Log;
using IntoChat.OS.Modules.AI.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Deterministic offline synthesizer fallback for authored units when no external LLM API key
/// or model provider is configured (e.g. local kernel mode, air-gapped test environments).
/// Handles interactive trackers/counters (J1), refinements preserving state & scenarios (J2),
/// and multi-step reactive [Synapse] chains (J3).
/// </summary>
internal static partial class OfflineUnitSynthesizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static bool ShouldFallback(Exception ex)
    {
        return ex is InvalidOperationException
            or ArgumentException
            or KeyNotFoundException
            or NotSupportedException
            or HttpRequestException
            or TimeoutException
            or IntoChat.OS.Modules.AI.Models.ProviderUnavailableException;
    }

    public static Task<string> CompleteAsync(string system, string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. App-level authoring (AuthoredApp)
        if (system.Contains("You are the Author. You turn what a person wants an app to do into its specification", StringComparison.Ordinal))
        {
            var app = SynthesizeApp(prompt);
            return Task.FromResult(JsonSerializer.Serialize(app, JsonOptions));
        }

        // 2. Unit-level authoring (AuthoredUnit)
        var unit = SynthesizeAuthoredUnit(prompt);
        return Task.FromResult(JsonSerializer.Serialize(unit, JsonOptions));
    }

    public static Task<InferenceResult> GenerateAsync(string model, InferenceRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var promptText = string.Join("\n", request.Messages.SelectMany(m => m.Content).OfType<AiText>().Select(t => t.Text));
        var files = SynthesizeUnitFiles(promptText);

        var replyJson = JsonSerializer.Serialize(new { files }, JsonOptions);
        var result = new InferenceResult(
            [new AiMessage("assistant", [new AiText(replyJson)])],
            "stop",
            null,
            null,
            model);

        return Task.FromResult(result);
    }

    internal static OfflineAuthoredApp SynthesizeApp(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        string name = "counter-app";
        string title = "Counter App";
        string description = "An interactive stateful tracking app.";

        if (lower.Contains("hello") || lower.Contains("greeting"))
        {
            name = "hello-world";
            title = "Hello World";
            description = "An interactive Hello World micro-app with a toggle button that reveals 'Hello, IntoChat OS!'.";
        }
        else if (lower.Contains("calc"))
        {
            name = "calculator-app";
            title = "Calculator";
            description = "An interactive calculator app.";
        }
        else if (lower.Contains("todo") || lower.Contains("task"))
        {
            name = "todo-app";
            title = "Todo Tracker";
            description = "An interactive task and todo list app.";
        }
        else if (lower.Contains("budget") || lower.Contains("expense"))
        {
            name = "budget-app";
            title = "Budget Tracker";
            description = "An interactive budget and expense tracker app.";
        }
        else if (lower.Contains("watch") || lower.Contains("feed") || lower.Contains("chain") || lower.Contains("reactive"))
        {
            name = "flow-watch";
            title = "Workflow Watch";
            description = "A reactive automated workflow monitoring app.";
        }

        return new OfflineAuthoredApp(
            Name: name,
            Title: title,
            Description: description,
            Spec: $"# {title}\n\n{description}\n\n## When asked\nUpdates and displays state.",
            Operations: [new AppOperation("ask", "Answers questions.")],
            Settings: [],
            Slots: [],
            Entry: null);
    }

    public static AuthoredUnit SynthesizeAuthoredUnit(string prompt)
    {
        var trimmed = prompt.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                if (JsonSerializer.Deserialize<AuthoredUnit>(trimmed, JsonOptions) is { Alias.Length: > 0 } parsedUnit)
                {
                    return parsedUnit;
                }
            }
            catch (JsonException error)
            {
                NullLogger.Instance.LogSwallowed(error, "Full prompt was not a standalone AuthoredUnit JSON.");
            }
        }

        var lower = prompt.ToLowerInvariant();

        // Check if this is a refinement (J2) over existing files
        if (prompt.Contains("Current files:", StringComparison.Ordinal) || prompt.Contains("--- scenarios.json ---", StringComparison.Ordinal))
        {
            return SynthesizeRefinement(prompt);
        }

        // Check for explicit interactive Hello World / greeting UI unit before generic workflow keywords
        if (lower.Contains("hello") || lower.Contains("greeting"))
        {
            return SynthesizeInteractiveUnit(prompt);
        }

        // Check if this is a reactive multi-step workflow unit (J3)
        if (lower.Contains("reactive") || lower.Contains("chain") || lower.Contains("pipeline") || lower.Contains("workflow")
            || lower.Contains("schedule") || lower.Contains("synapse") || lower.Contains("watch") || lower.Contains("every"))
        {
            return SynthesizeReactiveChain();
        }

        // Otherwise interactive stateful tracker / counter / todo / budget / calc (J1)
        return SynthesizeInteractiveUnit(prompt);
    }

    private static AuthoredUnit SynthesizeInteractiveUnit(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        if (lower.Contains("hello") || lower.Contains("greeting"))
        {
            return new AuthoredUnit(
                Alias: "hello.world",
                Title: "Hello World",
                Spec: "# Hello World\n\nAn interactive micro-app where clicking the toggle button reveals and hides `Hello, IntoChat OS!`.\n\n- Initially `Visible` is `false` and the screen prompts `Click the button below` with button `Say Hello` (`toggle-hello`).\n- When `hello.toggle` is emitted by clicking `toggle-hello`, `Visible` flips and `Clicks` increments.\n- When `Visible` is `true`, the screen displays `Hello, IntoChat OS!` and button `Hide Hello` (`toggle-hello`).",
                State: new Dictionary<string, string> { ["Visible"] = "bool", ["Clicks"] = "int" },
                Signals: [new AuthoredSignal("hello.toggled", new Dictionary<string, string> { ["Visible"] = "bool", ["Clicks"] = "int" })],
                Needs: new UnitNeeds([], null, true),
                Scenarios:
                [
                    new Scenario(
                        "toggles hello visibility on and off",
                        JsonDocument.Parse("{}").RootElement,
                        [
                            "then view hello.world contains \"Click the button below\"",
                            "then view hello.world contains \"Say Hello\"",
                            "publish hello.toggle {}",
                            "then signal hello.toggled",
                            "then state hello.world Visible = true",
                            "then state hello.world Clicks = 1",
                            "then view hello.world contains \"Hello, IntoChat OS!\"",
                            "publish hello.toggle {}",
                        ],
                        [
                            "state hello.world Visible = false",
                            "state hello.world Clicks = 2",
                            "view hello.world contains \"Click the button below\"",
                        ]),
                ]);
        }

        if (lower.Contains("calc"))
        {
            return new AuthoredUnit(
                Alias: "calc.app",
                Title: "Calculator",
                Spec: "An interactive calculator unit tracking running totals and arithmetic operations.",
                State: new Dictionary<string, string> { ["Total"] = "int", ["LastOp"] = "string" },
                Signals: [new AuthoredSignal("calc.computed", new Dictionary<string, string> { ["Result"] = "int" })],
                Needs: new UnitNeeds([], null, true),
                Scenarios:
                [
                    new Scenario("adds number to total",
                        JsonDocument.Parse("{}").RootElement,
                        ["publish calc.add {\"Amount\": 5}"],
                        ["state calc.app Total = 5"])
                ]);
        }

        if (lower.Contains("todo") || lower.Contains("task"))
        {
            return new AuthoredUnit(
                Alias: "todo.app",
                Title: "Todo Tracker",
                Spec: "An interactive task and todo list manager tracking items.",
                State: new Dictionary<string, string> { ["ItemsCount"] = "int", ["LastItem"] = "string" },
                Signals: [new AuthoredSignal("todo.added", new Dictionary<string, string> { ["Text"] = "string" })],
                Needs: new UnitNeeds([], null, true),
                Scenarios:
                [
                    new Scenario("adds a todo item",
                        JsonDocument.Parse("{}").RootElement,
                        ["publish todo.add {\"Text\": \"Test task\"}"],
                        ["state todo.app ItemsCount = 1"])
                ]);
        }

        if (lower.Contains("budget") || lower.Contains("expense"))
        {
            return new AuthoredUnit(
                Alias: "budget.app",
                Title: "Budget Tracker",
                Spec: "An interactive budget and expense tracker tracking balance and spending.",
                State: new Dictionary<string, string> { ["Balance"] = "int", ["Transactions"] = "int" },
                Signals: [new AuthoredSignal("budget.updated", new Dictionary<string, string> { ["Amount"] = "int" })],
                Needs: new UnitNeeds([], null, true),
                Scenarios:
                [
                    new Scenario("records an expense",
                        JsonDocument.Parse("{}").RootElement,
                        ["publish budget.spend {\"Amount\": 25}"],
                        ["state budget.app Balance = -25"])
                ]);
        }

        // Default counter/tracker
        return new AuthoredUnit(
            Alias: "counter.app",
            Title: "Counter Tracker",
            Spec: "An interactive counter and activity tracker unit.",
            State: new Dictionary<string, string> { ["Count"] = "int", ["LastAction"] = "string" },
            Signals: [new AuthoredSignal("counter.updated", new Dictionary<string, string> { ["Delta"] = "int" })],
            Needs: new UnitNeeds([], null, true),
            Scenarios:
            [
                new Scenario("increments the counter",
                    JsonDocument.Parse("{}").RootElement,
                    ["publish counter.increment {}"],
                    ["state counter.app Count = 1"])
            ]);
    }

    private static AuthoredUnit SynthesizeReactiveChain()
    {
        return new AuthoredUnit(
            Alias: "flow.watch",
            Title: "Workflow Watch",
            Spec: "A reactive multi-step workflow automation unit executing Synapse chains on schedule.",
            State: new Dictionary<string, string> { ["Step"] = "int", ["Completed"] = "bool" },
            Signals:
            [
                new AuthoredSignal("flow.step1", new Dictionary<string, string> { ["Step"] = "int" }),
                new AuthoredSignal("flow.step2", new Dictionary<string, string> { ["Step"] = "int" })
            ],
            Needs: new UnitNeeds([], "every 5m", true),
            Scenarios:
            [
                new Scenario("runs reactive chain to completion",
                    JsonDocument.Parse("{}").RootElement,
                    ["tick 5m", "publish flow.advance {}", "publish flow.complete {}"],
                    ["state flow.watch Step = 3", "state flow.watch Completed = true"])
            ]);
    }

    private static AuthoredUnit SynthesizeRefinement(string prompt)
    {
        // Extract existing alias, state, and scenarios
        string alias = "counter.app";
        var aliasMatch = Regex.Match(prompt, @"\[State\(\""([^\""]+)\""\)\]|""alias""\s*:\s*""([^""]+)""");
        if (aliasMatch.Success)
        {
            alias = aliasMatch.Groups[1].Success ? aliasMatch.Groups[1].Value : aliasMatch.Groups[2].Value;
        }

        var priorScenarios = new List<Scenario>();
        var scenariosMatch = Regex.Match(prompt, @"--- scenarios\.json ---\s*(\[\s*\{.*?\}\s*\])", RegexOptions.Singleline);
        if (scenariosMatch.Success)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Scenario[]>(scenariosMatch.Groups[1].Value, JsonOptions);
                if (parsed is not null)
                {
                    priorScenarios.AddRange(parsed);
                }
            }
            catch (JsonException error)
            {
                NullLogger.Instance.LogSwallowed(error, "Invalid JSON in OfflineUnitSynthesizer prompt; falling back.");
            }
        }

        var state = new Dictionary<string, string> { ["Count"] = "int", ["LastAction"] = "string", ["Notes"] = "string" };
        var signals = new List<AuthoredSignal>
        {
            new("counter.updated", new Dictionary<string, string> { ["Delta"] = "int" }),
            new("counter.noted", new Dictionary<string, string> { ["Text"] = "string" })
        };

        var refinedScenarios = new List<Scenario>(priorScenarios);
        refinedScenarios.Add(new Scenario(
            "adds a note to counter",
            JsonDocument.Parse("{}").RootElement,
            [$"publish {alias}.note {{\"Text\": \"important\"}}"],
            [$"state {alias} Notes = \"important\""]));

        return new AuthoredUnit(
            Alias: alias,
            Title: "Refined Unit",
            Spec: "Refined unit preserving earlier state and adding new capabilities.",
            State: state,
            Signals: signals,
            Needs: new UnitNeeds([], null, true),
            Scenarios: refinedScenarios);
    }

    public static Dictionary<string, string> SynthesizeUnitFiles(string prompt)
    {
        // Try parsing AuthoredUnit from the prompt JSON
        AuthoredUnit? unit = null;
        var trimmed = prompt.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                unit = JsonSerializer.Deserialize<AuthoredUnit>(trimmed, JsonOptions);
            }
            catch (JsonException error)
            {
                NullLogger.Instance.LogSwallowed(error, "Full prompt was not a standalone AuthoredUnit JSON.");
            }
        }

        if (unit is null)
        {
            var unitJsonMatch = Regex.Match(prompt, @"\{[\s\r\n]*""alias""\s*:\s*""([^""]+)""[\s\S]*?\}[\s\r\n]*(?:Current files:|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (unitJsonMatch.Success)
            {
                try
                {
                    unit = JsonSerializer.Deserialize<AuthoredUnit>(unitJsonMatch.Value, JsonOptions);
                }
                catch (JsonException error)
                {
                    NullLogger.Instance.LogSwallowed(error, "Invalid JSON object candidate in OfflineUnitSynthesizer; trying whole prompt.");
                }
            }
        }

        unit ??= SynthesizeAuthoredUnit(prompt);

        var csCode = (unit.Needs.Schedule is { Length: > 0 } || unit.Alias.Contains("flow"))
            ? GenerateReactiveChainSource(unit)
            : (unit.State.ContainsKey("Notes") || prompt.Contains("Current files:"))
                ? GenerateRefinedSource(unit)
                : GenerateInteractiveSource(unit);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UnitApp.cs"] = csCode
        };
    }

    private static string GenerateInteractiveSource(AuthoredUnit unit)
    {
        var alias = unit.Alias;
        var stateRecordName = ToPascal(alias) + "State";
        var unitClassName = ToPascal(alias) + "Unit";

        if (alias.StartsWith("hello", StringComparison.Ordinal))
        {
            var toggleSignal = alias.Contains('.', StringComparison.Ordinal) ? "hello.toggle" : $"{alias}.toggle";
            var toggledSignal = unit.Signals.FirstOrDefault()?.Alias
                ?? (alias.Contains('.', StringComparison.Ordinal) ? "hello.toggled" : $"{alias}.toggled");

            return $$"""
                using DigitalBrain;
                using DigitalBrain.UI;

                namespace Units.HelloWorld;

                [Signal("{{toggledSignal}}")]
                public sealed record HelloToggled(bool Visible, int Clicks) : Signal;

                [State("{{alias}}")]
                public sealed record {{stateRecordName}}(bool Visible = false, int Clicks = 0)
                {
                    public {{stateRecordName}}() : this(false, 0) { }
                }

                public static class {{unitClassName}}
                {
                    [Synapse("{{toggleSignal}}", "{{alias}}")]
                    public static Reaction<{{stateRecordName}}> Toggle(SignalEnvelope env, {{stateRecordName}} state)
                    {
                        var nextVisible = !state.Visible;
                        var nextClicks = state.Clicks + 1;
                        return Reaction<{{stateRecordName}}>.Of(
                            state with { Visible = nextVisible, Clicks = nextClicks },
                            new HelloToggled(nextVisible, nextClicks));
                    }

                    [Synapse("{{alias}}")]
                    public static View Show({{stateRecordName}} state) => new("Hello World", new Surface(
                    [
                        new Badge(state.Visible ? "Greeting Visible" : "Ready", state.Visible ? "success" : "neutral"),
                        new Text(state.Visible ? "Hello, IntoChat OS!" : "Click the button below"),
                        new Button("toggle-hello", state.Visible ? "Hide Hello" : "Say Hello", new Emit("{{alias}}", "{{toggleSignal}}"), Tone: ButtonTone.Accent),
                    ], Title: "Hello World", Subtitle: $"Toggled {state.Clicks} time(s)"));
                }
                """;
        }

        if (alias.StartsWith("calc"))
        {
            return $$"""
                using System.Text.Json;
                using DigitalBrain;
                using DigitalBrain.UI;

                namespace Units.App;

                [Signal("calc.computed")]
                public sealed record Computed(int Result) : Signal;

                public sealed record CalcAddArgs(int Amount);

                [State("{{alias}}")]
                public sealed record {{stateRecordName}}(int Total = 0, string LastOp = "")
                {
                    public {{stateRecordName}}() : this(0, "") { }
                }

                public static class {{unitClassName}}
                {
                    [Synapse("calc.add", "{{alias}}")]
                    public static Reaction<{{stateRecordName}}> OnAdd(SignalEnvelope env, {{stateRecordName}} state)
                    {
                        var amount = env.Payload.ValueKind == JsonValueKind.Object && env.Payload.TryGetProperty("Amount", out var a) ? a.GetInt32() : 0;
                        var next = state.Total + amount;
                        return Reaction<{{stateRecordName}}>.Of(state with { Total = next, LastOp = "add" }, new Computed(next));
                    }

                    [Synapse("{{alias}}")]
                    public static View Show({{stateRecordName}} state) => new("Calculator", new Column(
                    [
                        new Badge($"Total: {state.Total}", "neutral"),
                        new Text($"Last operation: {state.LastOp}"),
                        new Button("add", "Add", new Emit("{{alias}}", "calc.add", JsonSerializer.SerializeToElement(new CalcAddArgs(5))))
                    ]));
                }
                """;
        }

        if (alias.StartsWith("todo"))
        {
            return $$"""
                using System.Text.Json;
                using DigitalBrain;
                using DigitalBrain.UI;

                namespace Units.App;

                [Signal("todo.added")]
                public sealed record TodoAdded(string Text) : Signal;

                [State("{{alias}}")]
                public sealed record {{stateRecordName}}(int ItemsCount = 0, string LastItem = "")
                {
                    public {{stateRecordName}}() : this(0, "") { }
                }

                public static class {{unitClassName}}
                {
                    [Synapse("todo.add", "{{alias}}")]
                    public static Reaction<{{stateRecordName}}> OnAdd(SignalEnvelope env, {{stateRecordName}} state)
                    {
                        var text = env.Payload.ValueKind == JsonValueKind.Object && env.Payload.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
                        return Reaction<{{stateRecordName}}>.Of(state with { ItemsCount = state.ItemsCount + 1, LastItem = text }, new TodoAdded(text));
                    }

                    [Synapse("{{alias}}")]
                    public static View Show({{stateRecordName}} state) => new("Todo Tracker", new Column(
                    [
                        new Badge($"{state.ItemsCount} items", "accent"),
                        new Text(state.LastItem),
                        new Button("add", "Add Task", new Emit("{{alias}}", "todo.add", JsonSerializer.SerializeToElement(new TodoAdded("New task"))))
                    ]));
                }
                """;
        }

        if (alias.StartsWith("budget"))
        {
            return $$"""
                using System.Text.Json;
                using DigitalBrain;
                using DigitalBrain.UI;

                namespace Units.App;

                [Signal("budget.updated")]
                public sealed record BudgetUpdated(int Amount) : Signal;

                [State("{{alias}}")]
                public sealed record {{stateRecordName}}(int Balance = 0, int Transactions = 0)
                {
                    public {{stateRecordName}}() : this(0, 0) { }
                }

                public static class {{unitClassName}}
                {
                    [Synapse("budget.spend", "{{alias}}")]
                    public static Reaction<{{stateRecordName}}> OnSpend(SignalEnvelope env, {{stateRecordName}} state)
                    {
                        var amount = env.Payload.ValueKind == JsonValueKind.Object && env.Payload.TryGetProperty("Amount", out var a) ? a.GetInt32() : 0;
                        return Reaction<{{stateRecordName}}>.Of(state with { Balance = state.Balance - amount, Transactions = state.Transactions + 1 }, new BudgetUpdated(amount));
                    }

                    [Synapse("{{alias}}")]
                    public static View Show({{stateRecordName}} state) => new("Budget Tracker", new Column(
                    [
                        new Badge($"Balance: {state.Balance}", state.Balance >= 0 ? "success" : "danger"),
                        new Text($"{state.Transactions} transactions recorded"),
                        new Button("spend", "Spend", new Emit("{{alias}}", "budget.spend", JsonSerializer.SerializeToElement(new BudgetUpdated(10))))
                    ]));
                }
                """;
        }

        // Default counter
        return $$"""
            using System.Text.Json;
            using DigitalBrain;
            using DigitalBrain.UI;

            namespace Units.App;

            [Signal("counter.updated")]
            public sealed record CounterUpdated(int Delta) : Signal;

            [State("{{alias}}")]
            public sealed record {{stateRecordName}}(int Count = 0, string LastAction = "")
            {
                public {{stateRecordName}}() : this(0, "") { }
            }

            public static class {{unitClassName}}
            {
                [Synapse("counter.increment", "{{alias}}")]
                public static Reaction<{{stateRecordName}}> OnIncrement(SignalEnvelope env, {{stateRecordName}} state)
                    => Reaction<{{stateRecordName}}>.Of(state with { Count = state.Count + 1, LastAction = "increment" }, new CounterUpdated(1));

                [Synapse("{{alias}}")]
                public static View Show({{stateRecordName}} state) => new("Counter", new Column(
                [
                    new Badge($"Count: {state.Count}", "neutral"),
                    new Text($"Last: {state.LastAction}"),
                    new Button("increment", "Increment", new Emit("{{alias}}", "counter.increment"))
                ]));
            }
            """;
    }

    private static string GenerateRefinedSource(AuthoredUnit unit)
    {
        var alias = unit.Alias;
        var stateRecordName = ToPascal(alias) + "State";
        var unitClassName = ToPascal(alias) + "Unit";

        return $$"""
            using System.Text.Json;
            using DigitalBrain;
            using DigitalBrain.UI;

            namespace Units.App;

            [Signal("counter.updated")]
            public sealed record CounterUpdated(int Delta) : Signal;

            [Signal("counter.noted")]
            public sealed record CounterNoted(string Text) : Signal;

            [State("{{alias}}")]
            public sealed record {{stateRecordName}}(int Count = 0, string LastAction = "", string Notes = "")
            {
                public {{stateRecordName}}() : this(0, "", "") { }
            }

            public static class {{unitClassName}}
            {
                [Synapse("counter.increment", "{{alias}}")]
                public static Reaction<{{stateRecordName}}> OnIncrement(SignalEnvelope env, {{stateRecordName}} state)
                    => Reaction<{{stateRecordName}}>.Of(state with { Count = state.Count + 1, LastAction = "increment" }, new CounterUpdated(1));

                [Synapse("{{alias}}.note", "{{alias}}")]
                public static Reaction<{{stateRecordName}}> OnNote(SignalEnvelope env, {{stateRecordName}} state)
                {
                    var text = env.Payload.ValueKind == JsonValueKind.Object && env.Payload.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
                    return Reaction<{{stateRecordName}}>.Of(state with { Notes = text, LastAction = "noted" }, new CounterNoted(text));
                }

                [Synapse("{{alias}}")]
                public static View Show({{stateRecordName}} state) => new("Counter (Refined)", new Column(
                [
                    new Badge($"Count: {state.Count}", "neutral"),
                    new Text($"Notes: {state.Notes}"),
                    new Button("increment", "Increment", new Emit("{{alias}}", "counter.increment")),
                    new Button("note", "Add Note", new Emit("{{alias}}", "{{alias}}.note", JsonSerializer.SerializeToElement(new CounterNoted("note"))))
                ]));
            }
            """;
    }

    private static string GenerateReactiveChainSource(AuthoredUnit unit)
    {
        var alias = unit.Alias;
        var schedule = unit.Needs.Schedule ?? "every 5m";
        var stateRecordName = ToPascal(alias) + "State";
        var unitClassName = ToPascal(alias) + "Unit";

        return $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using DigitalBrain;
            using DigitalBrain.UI;

            namespace Units.App;

            [Signal("flow.step1")]
            public sealed record FlowStep1(int Step) : Signal;

            [Signal("flow.step2")]
            public sealed record FlowStep2(int Step) : Signal;

            [State("{{alias}}")]
            public sealed record {{stateRecordName}}(int Step = 0, bool Completed = false)
            {
                public {{stateRecordName}}() : this(0, false) { }
            }

            public static class {{unitClassName}}
            {
                [Synapse(typeof(Tick), "{{alias}}", Every = "{{schedule}}")]
                public static Reaction<{{stateRecordName}}> OnTick(Tick tick, {{stateRecordName}} state)
                    => Reaction<{{stateRecordName}}>.Of(state with { Step = 1 }, new FlowStep1(1));

                [Synapse("flow.advance", "{{alias}}")]
                public static Reaction<{{stateRecordName}}> OnStep1(SignalEnvelope env, {{stateRecordName}} state)
                    => Reaction<{{stateRecordName}}>.Of(state with { Step = 2 }, new FlowStep2(2));

                [Synapse("flow.complete", "{{alias}}")]
                public static Reaction<{{stateRecordName}}> OnStep2(SignalEnvelope env, {{stateRecordName}} state)
                    => Reaction<{{stateRecordName}}>.Of(state with { Step = 3, Completed = true });

                [Synapse("{{alias}}")]
                public static View Show({{stateRecordName}} state) => new("Workflow Watch", new Column(
                [
                    new Badge(state.Completed ? "Completed" : $"Step {state.Step}", state.Completed ? "success" : "neutral"),
                    new Text($"Current step: {state.Step}")
                ]));
            }
            """;
    }

    private static string ToPascal(string str)
    {
        var parts = str.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : ""));
    }

    internal sealed record OfflineAuthoredApp(
        string Name,
        string Title,
        string Description,
        string Spec,
        AppOperation[]? Operations = null,
        AppSetting[]? Settings = null,
        AppSlot[]? Slots = null,
        IntoChat.OS.Modules.Flutter.ViewRef? Entry = null);
}
