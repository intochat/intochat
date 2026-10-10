using DigitalBrain.Apps.Signals;
using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// A rule judges the would-be request before it is a fact: a rejection publishes AppRejected and an
// attention activity instead of AppRequested; an allowing rule, or no rules at all, changes nothing.
public sealed class AppRuleFacts
{
    private const string Brain = "app-rule-facts";

    [Fact]
    public async Task ARejectingRulePublishesAppRejectedAndNoAppRequested()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new JudgingSandbox(signal => Verdict.Reject("too late") with { Rule = "Curfew" });
        await using var brain = await Start(sandbox, ct);
        var app = await App(brain, "curfew", new() { ["synapses/answer.cs"] = "// s", ["rules/curfew.cs"] = "// r" });
        await using var requested = await brain.SubscribeAsync<AppRequested>(app, ct);
        await using var rejected = await brain.SubscribeAsync<AppRejected>(app, ct);
        await using var activity = await brain.SubscribeAsync<AppActivity>(app, ct);
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        silence.CancelAfter(TimeSpan.FromMilliseconds(300));
        await using var requests = requested.ReadAllAsync(silence.Token).GetAsyncEnumerator(silence.Token);
        await using var rejections = rejected.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        await using var activities = activity.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => app.Request("ask", "now"));

        Assert.Contains("too late", error.Message, StringComparison.Ordinal);
        Assert.True(await rejections.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(("ask", "Curfew", "too late"), (rejections.Current.Operation, rejections.Current.Rule, rejections.Current.Reason));
        Assert.True(await activities.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal("attention", activities.Current.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.MoveNextAsync().AsTask());
        Assert.IsType<AppRequested>(Assert.Single(sandbox.Judged));
    }

    [Fact]
    public async Task AnAllowingRuleLetsTheRequestThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new JudgingSandbox(_ => Verdict.Allow() with { Rule = "Open" });
        await using var brain = await Start(sandbox, ct);
        var app = await App(brain, "open", new() { ["synapses/answer.cs"] = "// s", ["rules/open.cs"] = "// r" });
        await using var requested = await brain.SubscribeAsync<AppRequested>(app, ct);
        await using var signals = requested.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        var id = await app.Request("ask", "now");

        Assert.True(await signals.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(id, signals.Current.RequestId);
    }

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

    [Fact]
    public void TheBuilderWritesRulesBeforeSynapses()
    {
        var rules = AgentPrompts.Builder.IndexOf("rules/", StringComparison.Ordinal);
        Assert.InRange(rules, 0, AgentPrompts.Builder.IndexOf("Keep each synapse", StringComparison.Ordinal));
        Assert.Contains("ILogic<", AgentPrompts.Builder, StringComparison.Ordinal);
    }

    private static AppManifest Manifest() => new("Rules", "judged", [new("ask", "ask it")], [], []);

    private static Task<ModuleBrain> Start(JudgingSandbox sandbox, CancellationToken ct)
        => ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IScriptSandbox>(sandbox))
            .StartAsync(ct);

    private static async Task<IApp> App(ModuleBrain brain, string name, Dictionary<string, string> files)
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
        public Task<Verdict[]> Judge(IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
        {
            Judged.Add(signal);
            return Task.FromResult<Verdict[]>([rule(signal)]);
        }
    }
}
