using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Operations;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Confirmation;
using IntoChat.OS.Kernel.Composition.Confirmations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration;

[Trait("host", "own")]
public sealed class ConfirmationRuntimeFacts
{
    [Fact]
    public async Task CancellingTheWaitingOperationInvalidatesItsCard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var plan = Plan();
        var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key("owner"));
        var asking = brain.Services.GetRequiredService<IConfirmationContexts>().For(new("owner"), "owner/files/main").Confirm(plan, cancelled.Token);
        await UntilPending(inbox, plan.Id, ct);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        await inbox.Resolve(plan.Id, true);
        Assert.Empty((await inbox.Read()).Pending);
        Assert.Equal(ConfirmationDenyReason.PlanInvalid, (await Entity(brain, "owner").Result(plan.Id))!.Reason);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("model")]
    [InlineData("neuron")]
    [InlineData("context")]
    public async Task AnAppAgentOrRelayedHumanClickCannotApprove(string origin)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var person = brain.Owner;
        var contexts = brain.Services.GetRequiredService<IConfirmationContexts>();
        var bound = contexts.For(new(person.BrainId), person.Key("files/main"));
        var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(person.BrainId));
        var plan = Plan();
        Task<ConfirmationResult> answer;
        using (person.AsApp(person.Key("apps/files"))) { answer = bound.Confirm(plan, ct); }
        using (person.AsCaller()) { await UntilPending(inbox, plan.Id, ct); }
        using (origin == "app" ? person.AsApp(person.Key("apps/files"))
            : origin == "model" ? person.AsModelImpersonatingCaller("conversation") : person.AsCaller())
        {
            if (origin == "context") { await bound.Resolve(plan.Id, true, ct); }
            else if (origin == "neuron")
            {
                var relay = DigitalBrain.Invocation.NeuronProxy.Create<IConfirmationInbox>(new(new(person.BrainId), "runtime.confirmations", "confirmations"),
                    KernelInvocationSink.For(brain.Services, person.Key("files/main")));
                await relay.Resolve(plan.Id, true);
            }
            else { await inbox.Resolve(plan.Id, true); }
        }
        Assert.Equal(ConfirmationDenyReason.SelfApproval, (await answer).Reason);
    }

    [Fact]
    public async Task AnAnswerAfterTheAbsoluteDeadlineExpiresEvenBeforeTheTimerFires()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        await using var brain = await TestBrain.Create().ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).StartAsync(ct);
        var person = brain.Owner;
        using var caller = person.AsCaller();
        var entity = Entity(brain, person.BrainId);
        var plan = Plan();
        await entity.Begin(plan, person.Key("files/main"));
        clock.Now += TimeSpan.FromMinutes(5);
        await brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(person.BrainId)).Resolve(plan.Id, true);
        Assert.Equal(ConfirmationOutcome.Expired, (await entity.Result(plan.Id))!.Outcome);
    }

    [Fact]
    public async Task PendingAndResolvedPlansSurviveRestartAndAnIdCannotBeReused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var plan = Plan();
        using (brain.Owner.AsCaller()) { await Entity(brain, "owner").Begin(plan, "owner/files/main"); }
        await brain.RestartAsync(ct);
        using (brain.Owner.AsCaller())
        {
            var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key("owner"));
            Assert.Equal(plan.Id, Assert.Single((await inbox.Read()).Pending).PlanId);
            await inbox.Resolve(plan.Id, true);
        }
        await brain.RestartAsync(ct);
        using (brain.Owner.AsCaller())
        {
            var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key("owner"));
            var revision = (await inbox.Read()).Revision;
            await inbox.Resolve(plan.Id, false);
            await inbox.Resolve("unknown", true);
            Assert.Equal(revision, (await inbox.Read()).Revision);
            Assert.Equal(ConfirmationOutcome.Approved, (await Entity(brain, "owner").Result(plan.Id))!.Outcome);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Entity(brain, "owner").Begin(plan, "owner/files/main"));
        }
    }

    [Fact]
    public async Task TheHostPolicyIsPerBrainAndDefaultsToDestructiveOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().ConfigureServices(services =>
            services.Configure<ConfirmationOptions>(options => options.Brains["owner"] = ConfirmationPolicy.Never)).StartAsync(ct);
        var contexts = brain.Services.GetRequiredService<IConfirmationContexts>();
        Assert.Equal(ConfirmationPolicy.Never, contexts.For(new("owner"), "owner/files/main").Policy);
        Assert.Equal(ConfirmationPolicy.DestructiveOnly, contexts.For(new("another"), "another/files/main").Policy);
        using (brain.Owner.AsCaller())
        {
            var result = await contexts.For(new("owner"), "owner/files/main").Confirm(Plan(), ct);
            Assert.True(result.Skipped);
            Assert.Equal(ConfirmationOutcome.Approved, result.Outcome);
        }
    }

    [Fact]
    public async Task TheRuntimeWaitsForADirectHumanAnswerAndIgnoresReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var person = await brain.PersonAsync("confirmation", ct);
        var contexts = brain.Services.GetService<IConfirmationContexts>();
        Assert.NotNull(contexts);
        var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(person.BrainId));
        var plan = new ConfirmationPlan("cf-" + Guid.NewGuid().ToString("N"), ConfirmationAction.Delete, true,
            "Delete report.txt?", "This removes report.txt.", "Delete", "Keep", "report.txt");
        Task<ConfirmationResult> answer;
        using (person.AsApp(person.Key("apps/files")))
        { answer = contexts.For(new(person.BrainId), person.Key("files/main")).Confirm(plan, ct); }
        using (person.AsCaller())
        {
            var pending = UntilPending(inbox, plan.Id, ct);
            if (await Task.WhenAny(pending, answer) == answer) { await answer; }
            await pending;
            Assert.False(answer.IsCompleted);
            await inbox.Resolve(plan.Id, true);
            var revision = (await inbox.Read()).Revision;
            await inbox.Resolve(plan.Id, false);
            Assert.Equal(revision, (await inbox.Read()).Revision);
            Assert.Empty((await inbox.Read()).Pending);
        }
        Assert.Equal(ConfirmationOutcome.Approved, (await answer).Outcome);
    }

    internal static async Task UntilPending(IConfirmationInbox inbox, string id, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!(await inbox.Read()).Pending.Any(item => item.PlanId == id))
        { await Task.Delay(20, timeout.Token); }
    }

    private static ConfirmationPlan Plan() => new("cf-" + Guid.NewGuid().ToString("N"), ConfirmationAction.Delete, true,
        "Delete report.txt?", "This removes report.txt.", "Delete", "Keep", "report.txt");

    private static IConfirmationEntity Entity(ITestBrain brain, string owner)
        => SiloNeurons.Get<IConfirmationEntity>(brain.Services, new global::DigitalBrain.NeuronId(new BrainId(owner), "runtime.confirmations", "confirmations"));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
