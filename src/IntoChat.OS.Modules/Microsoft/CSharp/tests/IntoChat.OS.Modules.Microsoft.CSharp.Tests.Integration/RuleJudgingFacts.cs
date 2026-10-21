using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Microsoft.CSharp;
using IntoChat.OS.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Microsoft.CSharp.Catalog;
using IntoChat.OS.Microsoft.CSharp.Files;
using IntoChat.OS.Microsoft.CSharp.Sandbox;
using IntoChat.OS.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// Judge compiles the bundle's shared classes here and runs each ILogic<T> whose T matches the signal in
// the brain's sandbox session (the real sandbox RuleJudge behind an in-process host). Rules fail closed:
// a rule that never answers, throws on load or does not compile is a rejection.
public sealed class RuleJudgingFacts
{
    private const string Brain = "rule-judging-facts";
    private const string Synapse = "Console.WriteLine(1);";

    private const string Rules = """
        using IntoChat.OS.Apps.Signals;

        public sealed class NoShouting : ILogic<AppRequested>
        {
            public Verdict Validate(AppRequested signal)
                => signal.Input.Any(char.IsLower) ? Verdict.Allow() : Verdict.Reject("no shouting");
        }

        public sealed class OnlyOnChanges : ILogic<AppChanged>
        {
            public Verdict Validate(AppChanged signal) => Verdict.Reject("never asked");
        }
        """;

    [Trait("host", "own")]
    [Fact]
    public async Task EveryMatchingRuleJudgesTheSignalInTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new JudgingHost();
        await using var brain = await Start(host, ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        var bundle = new Dictionary<string, string> { ["synapses/a.cs"] = Synapse, ["rules/shouting.cs"] = Rules };

        var shouted = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "HEY", Guid.NewGuid()), ct));
        var spoken = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "hey", Guid.NewGuid()), ct));

        Assert.Equal(("NoShouting", false, "no shouting"), (shouted.Rule, shouted.Allowed, shouted.Reason));
        Assert.True(spoken.Allowed);
        Assert.Equal(2, host.Judged.Count);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABundleWithAnAllowingRulePassesWhenNoSessionWasRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new JudgingHost { RequireSession = true };
        await using var brain = await Start(host, ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        var bundle = new Dictionary<string, string> { ["synapses/a.cs"] = Synapse, ["rules/shouting.cs"] = Rules };

        var spoken = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "hey", Guid.NewGuid()), ct));

        Assert.True(spoken.Allowed);
        Assert.Single(host.Started);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASessionThatCannotBeEnsuredRejectsWithRuleDidNotAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new JudgingHost { RequireSession = true, StartThrows = true };
        await using var brain = await Start(host, ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        var bundle = new Dictionary<string, string> { ["synapses/a.cs"] = Synapse, ["rules/shouting.cs"] = Rules };

        var verdict = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "hey", Guid.NewGuid()), ct));

        Assert.Equal(("rules", false, "rule did not answer"), (verdict.Rule, verdict.Allowed, verdict.Reason));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABundleWithoutRulesNeverReachesTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new JudgingHost();
        await using var brain = await Start(host, ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        var bundle = new Dictionary<string, string> { ["synapses/a.cs"] = Synapse, ["shared/text.cs"] = "static class Text { }" };

        Assert.Empty(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "HEY", Guid.NewGuid()), ct));
        Assert.Empty(host.Judged);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARuleThatThrowsOnLoadIsARejectionNamingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new JudgingHost(), ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        var bundle = new Dictionary<string, string>
        {
            ["synapses/a.cs"] = Synapse,
            ["rules/broken.cs"] = """
                public sealed class Broken : ILogic<IntoChat.OS.Apps.Signals.AppRequested>
                {
                    static Broken() => throw new InvalidOperationException("cannot start");
                    public Verdict Validate(IntoChat.OS.Apps.Signals.AppRequested signal) => Verdict.Allow();
                }
                """,
        };

        var verdict = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "hey", Guid.NewGuid()), ct));

        Assert.Equal(("Broken", false), (verdict.Rule, verdict.Allowed));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RulesThatDoNotCompileAreARejection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new JudgingHost(), ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        var bundle = new Dictionary<string, string> { ["synapses/a.cs"] = Synapse, ["rules/torn.cs"] = "public sealed class Torn : ILogic<Missing> { }" };

        var verdict = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "hey", Guid.NewGuid()), ct));

        Assert.False(verdict.Allowed);
        Assert.Contains("do not compile", verdict.Reason, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARuleThatLoopsForeverIsRejectedWithinTheTimeoutAndTheAppStaysResponsive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new JudgingHost(), ct, apps: true);
        var app = brain.Get<IApp>(IApp.Key(Brain, "looping"));
        await app.Create(new AppManifest("Looping", "", [new("ask", "")], [], []), "spec");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new()
        {
            ["synapses/a.cs"] = RuntimeBrain.Idle,
            ["rules/forever.cs"] = """
                public sealed class Forever : ILogic<IntoChat.OS.Apps.Signals.AppRequested>
                {
                    public Verdict Validate(IntoChat.OS.Apps.Signals.AppRequested signal)
                    { while (true) { Thread.Sleep(50); } }
                }
                """,
        }, null);
        var started = DateTimeOffset.UtcNow;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => app.Request("ask", "now"));

        Assert.Contains("rule did not answer", error.Message, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        await app.CurrentBuildAttempt();
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASecondRequestToAHungRuleIsRejectedAtOnceWithoutANewThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new JudgingHost();
        await using var brain = await Start(host, ct, ruleTimeout: TimeSpan.FromSeconds(3));
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        // The rule hangs on a gate the fact holds until it has the second answer, so "while the first run is still
        // blocked" is a fact of the test, not a measurement; the gate is released at the end so the thread ends.
        var gate = "rule-gate-" + Guid.NewGuid().ToString("N");
        using var released = new ManualResetEventSlim();
        AppDomain.CurrentDomain.SetData(gate, released);
        var bundle = new Dictionary<string, string>
        {
            ["synapses/a.cs"] = Synapse,
            ["rules/hung.cs"] = $$"""
                public sealed class Hung : ILogic<IntoChat.OS.Apps.Signals.AppRequested>
                {
                    public Verdict Validate(IntoChat.OS.Apps.Signals.AppRequested signal)
                    { ((ManualResetEventSlim)AppDomain.CurrentDomain.GetData("{{gate}}")!).Wait(); return Verdict.Allow(); }
                }
                """,
        };

        try
        {
            var first = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "a", Guid.NewGuid()), ct));
            var threads = host.ThreadsStarted;
            var second = Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "b", Guid.NewGuid()), ct));

            // "At once" means without waiting on the rule: the quarantined reason is only given before a run starts, the
            // thread count shows none did, and the gate shows the hung run was still blocked when the answer came.
            Assert.False(released.IsSet);
            Assert.Equal(("Hung", false, "rule did not answer"), (first.Rule, first.Allowed, first.Reason));
            Assert.Equal(("Hung", false, "rule did not answer (quarantined)"), (second.Rule, second.Allowed, second.Reason));
            Assert.Equal(threads, host.ThreadsStarted);
        }
        finally
        {
            released.Set();
            AppDomain.CurrentDomain.SetData(gate, null);
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASlowRuleRunsAgainOnceItsThreadFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new JudgingHost();
        await using var brain = await Start(host, ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        // The first run blocks on a gate the fact holds past the rule's budget, then releases; no fixed overrun.
        var gate = "rule-gate-" + Guid.NewGuid().ToString("N");
        using var released = new ManualResetEventSlim();
        AppDomain.CurrentDomain.SetData(gate, released);
        var bundle = new Dictionary<string, string>
        {
            ["synapses/a.cs"] = Synapse,
            ["rules/slow.cs"] = $$"""
                public sealed class Slow : ILogic<IntoChat.OS.Apps.Signals.AppRequested>
                {
                    private static int calls;
                    public Verdict Validate(IntoChat.OS.Apps.Signals.AppRequested signal)
                    {
                        if (Interlocked.Increment(ref calls) == 1) { ((ManualResetEventSlim)AppDomain.CurrentDomain.GetData("{{gate}}")!).Wait(); }
                        return Verdict.Allow();
                    }
                }
                """,
        };

        try
        {
            Assert.False(Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "a", Guid.NewGuid()), ct)).Allowed);
            released.Set();

            // Quarantined until the released thread ends; the next judging after that runs the rule again.
            var verdict = await TestWait.UntilAsync(async token => Assert.Single(await sandbox.Judge(Brain, bundle, new AppRequested("ask", "b", Guid.NewGuid()), token)),
                verdict => verdict.Reason != "rule did not answer (quarantined)", TimeSpan.FromSeconds(10), ct);
            Assert.True(verdict.Allowed);
        }
        finally { AppDomain.CurrentDomain.SetData(gate, null); }
    }

    // The production budget (3 s): the first ask also starts the session and fetches the rules, and at 1 s an allowing
    // rule was reported as "rules did not answer" on a loaded machine.
    private static Task<ITestBrain> Start(JudgingHost host, CancellationToken ct, bool apps = false, TimeSpan? ruleTimeout = null)
        => RuntimeBrain.Create(host, apps)
            .WithOptions<CSharpModule, CSharpOptions>(options => options.RuleTimeout = ruleTimeout ?? TimeSpan.FromSeconds(3))
            // The host downloads assemblies from its own store: the blob storage seam.
            .ConfigureServices(services => services.AddSingleton<IBehaviorAssemblies>(host))
            .StartAsync(ct);

    // The sandbox host's session API with its real RuleJudge, and the assembly store it downloads from.
    private sealed class JudgingHost : HttpMessageHandler, IBehaviorAssemblies
    {
        private const string Store = "http://assemblies.test/";
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        private readonly RuleJudge _judge;

        public JudgingHost()
        {
            var options = Options.Create(new SandboxOptions { CacheRoot = Path.Combine(Path.GetTempPath(), "rules-" + Guid.NewGuid().ToString("N")) });
            _judge = new(new BehaviorLoader(new HttpClient(this), options), options);
        }

        public int ThreadsStarted => _judge.ThreadsStarted;

        public ConcurrentQueue<JudgeRequest> Judged { get; } = new();

        public bool RequireSession { get; init; }

        public bool StartThrows { get; init; }

        public ConcurrentBag<string> Started { get; } = [];

        public Task Put(CompiledBehavior behavior, CancellationToken cancellationToken)
        {
            _blobs[behavior.Hash] = behavior.Assembly;
            _blobs[behavior.Hash + ".pdb"] = behavior.Symbols;
            return Task.CompletedTask;
        }

        public Task<bool> Exists(string hash, CancellationToken cancellationToken) => Task.FromResult(_blobs.ContainsKey(hash));

        public Task<Uri> ReadUrl(string name, TimeSpan lifetime, CancellationToken cancellationToken) => Task.FromResult(new Uri(Store + name));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var path = url.AbsolutePath.Trim('/');
            if (url.ToString().StartsWith(Store, StringComparison.Ordinal))
            { return _blobs.TryGetValue(path, out var bytes) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new(HttpStatusCode.NotFound); }
            switch (path.Split('/'))
            {
                case ["sessions", var id, "judge"]:
                    if (RequireSession && !Started.Contains(id)) { return new(HttpStatusCode.NotFound); }
                    var judge = (await request.Content!.ReadFromJsonAsync<JudgeRequest>(RuntimeProtocol.Json, cancellationToken))!;
                    Judged.Enqueue(judge);
                    var answer = await _judge.JudgeAsync(id, judge, cancellationToken);
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(answer, options: RuntimeProtocol.Json) };
                case ["sessions", var started] when request.Method == HttpMethod.Post:
                    if (StartThrows) { throw new TimeoutException("the session grain did not answer"); }
                    Started.Add(started);
                    return new(HttpStatusCode.Accepted);
                default:
                    return new(HttpStatusCode.NotFound);
            }
        }
    }
}
