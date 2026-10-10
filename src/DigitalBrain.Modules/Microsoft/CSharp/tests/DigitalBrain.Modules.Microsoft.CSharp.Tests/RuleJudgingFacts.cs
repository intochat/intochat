using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// Judge compiles the bundle's shared classes and runs each ILogic<T> whose T matches the signal;
// synapses and rules over other signals are not consulted, and each verdict names its rule.
public sealed class RuleJudgingFacts
{
    private const string Synapse = "Console.WriteLine(1);";

    private const string Rules = """
        using DigitalBrain.Apps.Signals;

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

    [Fact]
    public async Task EveryMatchingRuleJudgesTheSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().WithReminders().StartAsync(ct);
        var sandbox = brain.SiloServices.GetRequiredService<IScriptSandbox>();
        var bundle = new Dictionary<string, string> { ["synapses/a.cs"] = Synapse, ["rules/shouting.cs"] = Rules };

        var shouted = Assert.Single(await sandbox.Judge(bundle, new AppRequested("ask", "HEY", Guid.NewGuid()), ct));
        var spoken = Assert.Single(await sandbox.Judge(bundle, new AppRequested("ask", "hey", Guid.NewGuid()), ct));

        Assert.Equal(("NoShouting", false, "no shouting"), (shouted.Rule, shouted.Allowed, shouted.Reason));
        Assert.True(spoken.Allowed);
    }

    [Fact]
    public async Task ABundleWithoutRulesHasNoVerdicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().WithReminders().StartAsync(ct);
        var sandbox = brain.SiloServices.GetRequiredService<IScriptSandbox>();

        Assert.Empty(await sandbox.Judge(new Dictionary<string, string> { ["synapses/a.cs"] = Synapse }, new AppRequested("ask", "HEY", Guid.NewGuid()), ct));
    }
}
