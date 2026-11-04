using DigitalBrain;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Policies;

[DigitalBrain.Module("charges.test.apps")]
public sealed class PolicyWriterTestModule : DigitalBrain.Module
{
    public static readonly ActionDescriptor Charges = new("charges.charge", "charges", "USD");
    public static readonly ActionDescriptor Drafts = new("gmail.draft.create", "create a mail draft", null);

    public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], [])
    {
        Actions = [Charges, Drafts],
    };
}

public sealed class PolicyWriterFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TestProxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }

    private static TestOS Start()
        => TestOS.Local().Register(typeof(PolicyWriterTestModule).Assembly);

    private static PolicyWriter CreateWriter(TestOS os, FakePolicyModel model)
        => new(model, new TestProxies(os), os.Clock, NullLogger<PolicyWriter>.Instance);

    [Fact]
    public async Task TheWeeklyCapSentenceBecomesOnePendingProposalWithTheRenderedText()
    {
        await using var os = Start();
        var model = new FakePolicyModel(
            """{"action":"charges.charge","measure":"Sum","window":"Week","limit":300,"condition":null}""");
        var writer = CreateWriter(os, model);

        var outcome = await writer.Write("b", "do not spend more than $300 this week", Ct);

        Assert.Equal(ProposalStatus.Pending, outcome.Status);
        Assert.Equal("Allow at most 300 USD of charges per week", outcome.Text);
        Assert.NotNull(outcome.RuleId);
        Assert.NotNull(outcome.PlanId);
        var waiting = Assert.Single(os.Confirmations.Waiting);
        Assert.Equal(outcome.PlanId, waiting.PlanId);
        Assert.Equal("Adopt a promise", waiting.Plan.Title);
        Assert.Equal("Allow at most 300 USD of charges per week", waiting.Plan.Body);
        Assert.True(waiting.Plan.RequiresPerson);
        Assert.Equal(1, model.Calls);
        Assert.Contains("charges.charge", model.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("USD", model.Prompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUndeclaredActionIsRefusedWithoutAsking()
    {
        await using var os = Start();
        var model = new FakePolicyModel(
            """{"action":"wire.transfer","measure":"Sum","window":"Week","limit":300,"condition":null}""");
        var writer = CreateWriter(os, model);

        var outcome = await writer.Write("b", "do not wire more than $300 this week", Ct);

        Assert.Equal(ProposalStatus.Refused, outcome.Status);
        Assert.Null(outcome.RuleId);
        Assert.Null(outcome.PlanId);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("unknown action 'wire.transfer'", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(os.Confirmations.Waiting);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task AGrammarErrorGoesBackToTheModelOnce()
    {
        await using var os = Start();
        var model = new FakePolicyModel(
            """{"action":"charges.charge","measure":"Sum","window":"Fortnight","limit":300,"condition":null}""",
            """{"action":"charges.charge","measure":"Sum","window":"Week","limit":300,"condition":null}""");
        var writer = CreateWriter(os, model);

        var outcome = await writer.Write("b", "do not spend more than $300 this week", Ct);

        Assert.Equal(ProposalStatus.Pending, outcome.Status);
        Assert.Equal("Allow at most 300 USD of charges per week", outcome.Text);
        Assert.Equal(2, model.Calls);
        Assert.Contains("grammar", model.Prompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.Single(os.Confirmations.Waiting);
    }

    [Fact]
    public async Task ARefusalLineIsReported()
    {
        await using var os = Start();
        var model = new FakePolicyModel("REFUSE: no module charges money");
        var writer = CreateWriter(os, model);

        var outcome = await writer.Write("b", "do not spend more than $300 this week", Ct);

        Assert.Equal(ProposalStatus.Refused, outcome.Status);
        Assert.Equal("no module charges money", outcome.Reason);
        Assert.Empty(os.Confirmations.Waiting);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task AConditionedRuleIsParsedAndProposed()
    {
        await using var os = Start();
        var model = new FakePolicyModel(
            """{"action":"gmail.draft.create","measure":"Count","window":"Day","limit":5,"condition":{"fact":"To","op":"EndsWith","value":"@corp.com"}}""");
        var writer = CreateWriter(os, model);

        var outcome = await writer.Write("b", "at most 5 drafts a day to @corp.com", Ct);

        Assert.Equal(ProposalStatus.Pending, outcome.Status);
        Assert.Equal("Allow at most 5 count of create a mail draft per day when To EndsWith @corp.com", outcome.Text);
        Assert.Single(os.Confirmations.Waiting);
    }
}
