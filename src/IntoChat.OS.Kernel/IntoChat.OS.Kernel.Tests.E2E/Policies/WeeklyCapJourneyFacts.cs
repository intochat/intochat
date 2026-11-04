using System.Net;
using System.Net.Http.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Confirmation;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Modules.Apps;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Tests.E2E.Policies;

public sealed class WeeklyCapJourneyFacts(ProductHostFixture fixture)
    : IClassFixture<ProductHostFixture>
{
    private const string WeeklyThreeHundredRow =
        """{"action":"charges.charge","measure":"Sum","window":"Week","limit":300,"condition":null}""";

    private const string ExpectedRuleText =
        "Allow at most 300 USD of charges per week";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWeeklyCapPromiseHoldsFromSentenceToRefusedCharge()
    {
        var brain = fixture.NewBrain("weeklycap");
        using var stamp = StampPerson(brain);
        fixture.EnqueuePolicyReply(WeeklyThreeHundredRow);

        // 1. POST /policies "do not spend more than $300 this week" -> 200 Pending with Text "Allow at most 300 USD of charges per week"
        using var postResponse = await fixture.Http.PostAsJsonAsync(
            $"/brains/{brain}/policies",
            new PoliciesEndpoints.TextRequest("do not spend more than $300 this week"),
            Ct);
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        var outcome = await postResponse.Content.ReadFromJsonAsync<ProposalOutcome>(Ct);
        Assert.NotNull(outcome);
        Assert.Equal(ProposalStatus.Pending, outcome.Status);
        Assert.Equal(ExpectedRuleText, outcome.Text);
        Assert.NotNull(outcome.RuleId);
        Assert.NotNull(outcome.PlanId);

        // 2. IConfirmationInbox.Read(): one Pending whose Plan.Body equals that text; Resolve(planId, true)
        var inbox = ConfirmationInbox(brain);
        await Until(async () => (await inbox.Read()).Pending.Length == 1, Ct);
        var pending = Assert.Single((await inbox.Read()).Pending);
        Assert.Equal(outcome.PlanId, pending.PlanId);
        Assert.Equal(ExpectedRuleText, pending.Plan.Body);

        RequestContext.Set(KernelInvocationSink.CallerKey, "");
        await inbox.Resolve(pending.PlanId, approve: true);

        // 3. GET /policies shows the rule
        await Until(async () => (await GetPoliciesAsync(brain)).Count == 1, Ct);
        var adopted = Assert.Single(await GetPoliciesAsync(brain));
        Assert.Equal(outcome.RuleId, adopted.Id);
        Assert.Equal(ExpectedRuleText, adopted.Text);

        // 4. Charge(120), (100), (70) succeed; Charge(20) throws RuleViolationException whose message contains the text
        var charges = Charges(brain);
        Assert.Equal(1, await charges.Charge(120m, Ct));
        Assert.Equal(2, await charges.Charge(100m, Ct));
        Assert.Equal(3, await charges.Charge(70m, Ct));

        var violation = await Assert.ThrowsAsync<RuleViolationException>(() => charges.Charge(20m, Ct));
        Assert.Contains(ExpectedRuleText, violation.Message, StringComparison.Ordinal);

        // 5. DELETE /policies/{id} -> Charge(20) succeeds
        using var deleteResponse = await fixture.Http.DeleteAsync($"/brains/{brain}/policies/{outcome.RuleId}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.Empty(await GetPoliciesAsync(brain));

        Assert.Equal(4, await charges.Charge(20m, Ct));
    }

    [Fact]
    public async Task ADeniedPromiseChangesNothing()
    {
        var brain = fixture.NewBrain("deniedcap");
        using var stamp = StampPerson(brain);
        fixture.EnqueuePolicyReply(WeeklyThreeHundredRow);

        using var postResponse = await fixture.Http.PostAsJsonAsync(
            $"/brains/{brain}/policies",
            new PoliciesEndpoints.TextRequest("do not spend more than $300 this week"),
            Ct);
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        var outcome = await postResponse.Content.ReadFromJsonAsync<ProposalOutcome>(Ct);
        Assert.NotNull(outcome);
        Assert.Equal(ProposalStatus.Pending, outcome.Status);
        Assert.NotNull(outcome.PlanId);

        var inbox = ConfirmationInbox(brain);
        await Until(async () => (await inbox.Read()).Pending.Length == 1, Ct);
        var pending = Assert.Single((await inbox.Read()).Pending);
        Assert.Equal(outcome.PlanId, pending.PlanId);

        RequestContext.Set(KernelInvocationSink.CallerKey, "");
        await inbox.Resolve(pending.PlanId, approve: false);

        await Until(async () => (await inbox.Read()).Pending.Length == 0 && await PoliciesPendingCountAsync(brain) == 0, Ct);
        Assert.Empty(await GetPoliciesAsync(brain));

        var charges = Charges(brain);
        Assert.Equal(1, await charges.Charge(350m, Ct));
    }

    private async Task<int> PoliciesPendingCountAsync(string brain)
    {
        var grains = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IGrainFactory>(fixture.Services);
        var host = grains.GetGrain<INeuronHost>(GrainId.Create("neuron.host", IPolicies.Key(brain)));
        var state = await host.InspectState(Ct);
        return state is { } json && json.TryGetProperty("pending", out var pending)
            ? pending.GetArrayLength()
            : 0;
    }

    private IConfirmationInbox ConfirmationInbox(string brain)
        => SiloNeurons.Get<IConfirmationInbox>(
            fixture.Services,
            new NeuronId(new BrainId(brain), "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));

    private ICharges Charges(string brain)
        => SiloNeurons.Get<ICharges>(
            fixture.Services,
            NeuronId.Parse(ICharges.Key(brain)));

    private async Task<IReadOnlyList<PolicyRule>> GetPoliciesAsync(string brain)
    {
        using var response = await fixture.Http.GetAsync($"/brains/{brain}/policies", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<PolicyRule[]>(Ct) ?? [];
    }

    private static IDisposable StampPerson(string brain)
    {
        RequestContext.Set(KernelInvocationSink.CallerKey, "");
        return RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));
    }

    private static async Task Until(Func<Task<bool>> predicate, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(25, ct);
        }

        Assert.True(await predicate(), "Timed out waiting for silo state convergence.");
    }
}
