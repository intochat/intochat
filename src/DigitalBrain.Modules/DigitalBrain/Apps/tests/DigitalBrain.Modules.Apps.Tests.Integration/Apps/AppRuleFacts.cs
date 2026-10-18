using DigitalBrain.Apps.Signals;
using DigitalBrain.Modules.Apps.Tests.Integration.Runtimes;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// A rule judges the would-be request before it is a fact: a rejection publishes AppRejected and an
// attention activity instead of AppRequested; an allowing rule, or no rules at all, changes nothing.
public sealed class AppRuleFacts
{
    private const string Brain = "app-rule-facts";

    [Trait("host", "own")]
    [Fact]
    public async Task ARejectingRulePublishesAppRejectedAndNoAppRequested()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new JudgingSandbox(signal => signal is AppRequested { Input: "now" } ? Verdict.Reject("too late") with { Rule = "Curfew" } : Verdict.Allow());
        await using var brain = await Start(sandbox, ct);
        var app = await App(brain, "curfew", new() { ["synapses/answer.cs"] = "// s", ["rules/curfew.cs"] = "// r" });
        await using var requested = await brain.Observe<AppRequested>(app, ct);
        await using var rejected = await brain.Observe<AppRejected>(app, ct);
        await using var activity = await brain.Observe<AppActivity>(app, ct);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => app.Request("ask", "now"));

        Assert.Contains("too late", error.Message, StringComparison.Ordinal);
        var rejection = await rejected.NextAsync(ct: ct);
        Assert.Equal(("ask", "Curfew", "too late"), (rejection.Operation, rejection.Rule, rejection.Reason));
        Assert.Equal("attention", (await activity.NextAsync(ct: ct)).State);
        var presentation = (await app.Read()).Presentation;
        Assert.NotNull(presentation);
        Assert.Equal("false", presentation.Blackboard["running"]);
        Assert.Equal("true", presentation.Blackboard["attention"]);
        Assert.IsType<AppRequested>(Assert.Single(sandbox.Judged));
        // The next allowed request is the first AppRequested: the rejected one was never published.
        var later = await app.Request("ask", "later");
        Assert.Equal(later, (await requested.NextAsync(ct: ct)).RequestId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAllowingRuleLetsTheRequestThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new JudgingSandbox(_ => Verdict.Allow() with { Rule = "Open" });
        await using var brain = await Start(sandbox, ct);
        var app = await App(brain, "open", new() { ["synapses/answer.cs"] = "// s", ["rules/open.cs"] = "// r" });
        await using var requested = await brain.Observe<AppRequested>(app, ct);

        var id = await app.Request("ask", "now");

        Assert.Equal(id, (await requested.NextAsync(ct: ct)).RequestId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppWithoutAProgramIsNeverJudged()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new JudgingSandbox(_ => Verdict.Reject("never"));
        await using var brain = await Start(sandbox, ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "bare"));
        await app.Create(Manifest(), "spec");

        Assert.NotEqual(Guid.Empty, await app.Request("ask", "now"));
        Assert.Empty(sandbox.Judged);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WithoutASandboxAProgramThatDeclaresARuleIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(ct);
        var app = await App(brain, "unjudged", new()
        {
            ["synapses/answer.cs"] = "Console.WriteLine(1);",
            ["rules/curfew.cs"] = "public sealed class Curfew : ILogic<DigitalBrain.Apps.Signals.AppRequested> { public Verdict Validate(DigitalBrain.Apps.Signals.AppRequested r) => Verdict.Allow(); }",
        });

        var error = await Assert.ThrowsAsync<ArgumentException>(() => app.Request("ask", "now"));

        Assert.Contains("no sandbox", error.Message, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WithoutASandboxAProgramWithoutRulesPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(ct);
        var app = await App(brain, "plain", new() { ["synapses/answer.cs"] = "Console.WriteLine(1);", ["shared/text.cs"] = "static class Text { }" });

        Assert.NotEqual(Guid.Empty, await app.Request("ask", "now"));
    }

    [Fact]
    public void TheBuilderWritesRulesBeforeSynapses()
    {
        var rules = AgentPrompts.Builder.IndexOf("rules/", StringComparison.Ordinal);
        Assert.InRange(rules, 0, AgentPrompts.Builder.IndexOf("Keep each synapse", StringComparison.Ordinal));
        Assert.Contains("ILogic<", AgentPrompts.Builder, StringComparison.Ordinal);
    }

    private static AppManifest Manifest() => new("Rules", "judged", [new("ask", "ask it")], [], []);

    private static Task<ITestBrain> Start(JudgingSandbox sandbox, CancellationToken ct)
        => TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new())
            // Rules are judged in the script sandbox; the fake hands back the fact's verdict.
            .ConfigureServices(services => services.AddSingleton<IScriptSandbox>(sandbox))
            .StartAsync(ct);

    private static async Task<IApp> App(ITestBrain brain, string name, Dictionary<string, string> files)
    {
        var app = brain.Get<IApp>(IApp.Key(Brain, name));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), files, null);
        return app;
    }

    private sealed class JudgingSandbox(Func<Signal, Verdict> rule) : IScriptSandbox
    {
        public List<Signal> Judged { get; } = [];
        public bool CanRun => true;
        public string AuthoringDescription => "";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptCompilationCheck(true, []));
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
        {
            Judged.Add(signal);
            return Task.FromResult<Verdict[]>([rule(signal)]);
        }
    }
}
