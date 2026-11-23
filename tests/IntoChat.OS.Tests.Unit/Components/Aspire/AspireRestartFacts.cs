using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Testing;
using IntoChat.Aspire;
using Microsoft.Extensions.DependencyInjection;
using TestEdge = IntoChat.OS.Tests.TestEdge;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

// IAspire.Restart: a direct person restarts at once; anyone else gets "pending" and the restart runs when a person approves through
// the real confirmations neuron. The kernel, unknown names and a missing AppHost are refused. Asking never holds the neuron's turn.
public sealed class AspireRestartFacts
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId Aspire = new(Brain, "aspire", "aspire");

    [Fact]
    public async Task ARestartOfAKnownResourceRunsTheCommandAndPublishesRestarted()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart("web-searxng"));

        Assert.True(result.Accepted);
        Assert.Equal([("web-searxng", AspireMcp.RestartCommand)], tools.Commands);
        Assert.Equal([new AspireResourceRestarted("web-searxng", os.Clock.GetUtcNow())], Signals<AspireResourceRestarted>(os));
    }

    [Fact]
    public async Task AnUnknownOrOldNameIsRefusedWithTheCurrentNames()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart("OperatingSystem-a"));

        Assert.False(result.Accepted);
        Assert.Equal("unknown", result.Reason);
        Assert.Equal(["web-searxng", "os"], result.Known!);
        Assert.Empty(tools.Commands);
    }

    [Theory]
    [InlineData("Web-Searxng")]
    [InlineData(" web-searxng")]
    [InlineData("web-searxng ")]
    [InlineData("web")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ANameThatDiffersOnlyByCaseOrWhitespaceIsUnknown(string name)
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart(name));

        Assert.Equal("unknown", result.Reason);
        Assert.Empty(tools.Commands);
    }

    [Theory]
    [InlineData("os")]
    [InlineData("os-a")]
    [InlineData("OperatingSystem-a")]
    [InlineData("os-gateway")]
    [InlineData("OperatingSystem-front")]
    [InlineData("os-rebuilder")]
    [InlineData("OperatingSystem-a-rebuilder")]
    public async Task TheKernelItselfIsRefused(string kernel)
    {
        var tools = new FakeTools("web-searxng", kernel);
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart(kernel));

        Assert.False(result.Accepted);
        Assert.Equal("self", result.Reason);
        Assert.Empty(tools.Commands);
    }

    // The live list may name this process in a shape the name rules do not know: its own instance name is refused too, with its rebuilder.
    [Theory]
    [InlineData("kernel-7")]
    [InlineData("kernel-7-rebuilder")]
    public async Task ThisProcessesOwnInstanceIsRefusedWhateverItsName(string name)
    {
        var tools = new FakeTools("web-searxng", "kernel-7", "kernel-7-rebuilder");
        await using var os = Host(tools, self: "kernel-7");

        var result = await Call(os, Person(), aspire => aspire.Restart(name));

        Assert.Equal("self", result.Reason);
        Assert.Empty(tools.Commands);
    }

    // The reviewer's orphan: the confirmations neuron refuses the question (here TooManyWaiting for the Aspire neuron as raiser).
    // Nothing is stored or announced, the caller hears "declined" with the refusal, and once the quota frees the next request asks
    // again instead of answering a dead plan.
    [Fact]
    public async Task ARequestTheConfirmationsRefuseLeavesNothingPending()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var others = await FillTheQuotaOfAspire(os);

        var refused = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("declined", refused.Reason);
        Assert.Null(refused.Confirmation);
        Assert.Contains("confirmations waiting", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Signals<AspireResourceRestartRequested>(os));
        Assert.DoesNotContain(os.Confirmations.Waiting, waiting => waiting.PlanId.StartsWith(AspireNeuron.RestartKind, StringComparison.Ordinal));

        await Answer(os, Person(), others[0].Id, approve: false);
        var again = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("pending", again.Reason);
        Assert.Contains(os.Confirmations.Waiting, waiting => waiting.PlanId == again.Confirmation);
        Assert.Equal(again.Confirmation, Assert.Single(Signals<AspireResourceRestartRequested>(os)).Confirmation);
    }

    // The confirmations neuron keeps the last ConfirmationState.MaxEntries answers: a resolved plan it no longer has a record of can
    // never be read, so the waiting restart ends as declined. The neuron first activates after the record is gone.
    [Fact]
    public async Task AnAnswerWithNoRecordBehindItDropsThePendingRestart()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var now = os.Clock.GetUtcNow().ToString("O");
        os.SeedStoredState(Aspire, "{\"resources\":[],\"structuredLogs\":[],\"traces\":[],\"pendingRestarts\":[{\"planId\":\"ghost\",\"resource\":\"web-searxng\",\"requestedAt\":\"" + now + "\"}]}");
        await Call(os, Person(), async aspire => (await aspire.GetResourcesAsync()).Length);
        await os.Deactivate(Aspire);
        var confirmations = os.Get<IConfirmationRequests>(IConfirmationRequests.Of(Brain));
        using (TestEdge.Authority.Enter(Person()))
        {
            foreach (var id in (string[])["ghost", .. Enumerable.Range(0, ConfirmationState.MaxEntries).Select(index => $"later:{index}")])
            {
                await confirmations.Request(Plan(id));
                await Answer(os, Person(), id, approve: false);
            }
            Assert.Null(await confirmations.Outcome("ghost"));
        }
        Assert.False(os.IsActive(Aspire));

        await Call(os, Person(), async aspire => (await aspire.GetResourcesAsync()).Length);

        await Until(() => Signals<AspireResourceRestartRefused>(os).Length == 1);
        Assert.Equal([new AspireResourceRestartRefused("web-searxng", "declined")], Signals<AspireResourceRestartRefused>(os));
        Assert.Empty(tools.Commands);
    }


    // A pending entry older than three confirmation expiries has lost its answer: it is dropped as expired and the resource asks again.
    [Fact]
    public async Task AStalePendingRestartIsDroppedAndTheResourceCanBeAskedAgain()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var stale = (os.Clock.GetUtcNow() - TimeSpan.FromMinutes(16)).ToString("O");
        os.SeedStoredState(Aspire, "{\"resources\":[],\"structuredLogs\":[],\"traces\":[],\"pendingRestarts\":[{\"planId\":\"aspire.resource.restart:web-searxng:dead\",\"resource\":\"web-searxng\",\"requestedAt\":\"" + stale + "\"}]}");

        var result = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("pending", result.Reason);
        Assert.NotEqual("aspire.resource.restart:web-searxng:dead", result.Confirmation);
        Assert.Equal([new AspireResourceRestartRefused("web-searxng", "declined", "expired")], Signals<AspireResourceRestartRefused>(os));
    }

    // Stored by the previous head, an entry has no requestedAt: it loads, reads as stale and is dropped the same way.
    [Fact]
    public async Task AnEntryStoredWithoutRequestedAtLoadsAndIsDroppedAsStale()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        os.SeedStoredState(Aspire, "{\"resources\":[],\"structuredLogs\":[],\"traces\":[],\"pendingRestarts\":[{\"planId\":\"aspire.resource.restart:web-searxng:old\",\"resource\":\"web-searxng\"}]}");

        var result = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("pending", result.Reason);
        Assert.NotEqual("aspire.resource.restart:web-searxng:old", result.Confirmation);
        Assert.Equal("expired", Assert.Single(Signals<AspireResourceRestartRefused>(os)).Message);
    }

    // The confirmation gate is an optional slot: the activation check a runtime runs (Slots.Resolve, as TestNeuronHost and the Orleans
    // host call it) passes when no confirmations neuron is hosted, and the neuron itself still reads and answers.
    [Fact]
    public async Task TheAspireNeuronStillWorksWhereConfirmationsAreNotHosted()
    {
        var needs = DigitalBrain.Components.ComponentManifests.SlotsOf(typeof(AspireNeuron));
        var need = Assert.Single(needs, need => need.Contract == typeof(IConfirmationRequests));
        Assert.False(need.Required);
        var resolved = DigitalBrain.Components.Slots.Resolve(Brain, new DigitalBrain.Components.ComponentId("aspire"), needs, null, _ => false);
        Assert.DoesNotContain(resolved, binding => binding.Slot == need.Name);

        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        Assert.NotEmpty(await Call(os, Person(), aspire => aspire.GetResourcesAsync()));
    }

    [Fact]
    public async Task WithoutAnAppHostItIsRefused()
    {
        var tools = new FakeTools("web-searxng") { Available = false };
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("unavailable", result.Reason);
        Assert.Equal(0, tools.Calls);
        Assert.Empty(tools.Commands);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("app")]
    [InlineData("person-in-a-conversation")]
    public async Task ACallerThatIsNotAPersonGetsPendingAndNothingRuns(string kind)
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        var result = await Call(os, NotAPerson(kind), aspire => aspire.Restart("web-searxng"));

        Assert.False(result.Accepted);
        Assert.Equal("pending", result.Reason);
        Assert.Equal(result.Confirmation, Assert.Single(os.Confirmations.Waiting).PlanId);
        Assert.Empty(tools.Commands);
        Assert.Empty(Signals<AspireResourceRestarted>(os));
    }

    [Fact]
    public async Task ThePersonsApprovalRunsThePendingRestart()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var pending = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        await Answer(os, Person(), pending.Confirmation!, approve: true);

        await Until(() => tools.Commands.Count == 1);
        Assert.Equal([("web-searxng", AspireMcp.RestartCommand)], tools.Commands);
        Assert.Single(Signals<AspireResourceRestarted>(os));
    }

    [Fact]
    public async Task ThePersonsDenialPublishesDeclinedAndNothingRuns()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var pending = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        await Answer(os, Person(), pending.Confirmation!, approve: false);

        await Until(() => Signals<AspireResourceRestartRefused>(os).Length == 1);
        Assert.Equal([new AspireResourceRestartRefused("web-searxng", "declined")], Signals<AspireResourceRestartRefused>(os));
        Assert.Empty(tools.Commands);
    }

    [Fact]
    public async Task AnExpiredQuestionPublishesDeclinedAndNothingRuns()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        os.Clock.Advance(TimeSpan.FromMinutes(6));

        await Until(() => Signals<AspireResourceRestartRefused>(os).Length == 1);
        Assert.Equal("declined", Signals<AspireResourceRestartRefused>(os)[0].Reason);
        Assert.Empty(tools.Commands);
    }

    [Fact]
    public async Task AQuestionThePolicySkipsIsDeclinedAndNothingRuns()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools, ConfirmationPolicy.Never);

        var result = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("declined", result.Reason);
        Assert.Equal("declined", Assert.Single(Signals<AspireResourceRestartRefused>(os)).Reason);
        Assert.Empty(tools.Commands);
    }

    // The approval goes to the real confirmations neuron as the agent itself: the Answerer rule refuses it and the question stays open.
    [Fact]
    public async Task AnAgentCannotApproveItsOwnRestart()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var agent = NotAPerson("agent");
        var pending = await Call(os, agent, aspire => aspire.Restart("web-searxng"));

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Answer(os, agent, pending.Confirmation!, approve: true));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Answer(os, NotAPerson("app"), pending.Confirmation!, approve: true));
        await os.Idle();

        Assert.Contains(os.Confirmations.Waiting, waiting => waiting.PlanId == pending.Confirmation);
        Assert.Empty(tools.Commands);
        Assert.Empty(Signals<AspireResourceRestarted>(os));
    }

    [Theory]
    [InlineData("gone", "unknown")]
    [InlineData("unavailable", "unavailable")]
    public async Task AResourceThatChangedBeforeTheApprovalIsRefused(string change, string reason)
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var pending = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));
        switch (change)
        {
            case "gone": tools.Names = ["os"]; break;
            default: tools.Available = false; break;
        }

        await Answer(os, Person(), pending.Confirmation!, approve: true);

        await Until(() => Signals<AspireResourceRestartRefused>(os).Length == 1);
        Assert.Equal(reason, Signals<AspireResourceRestartRefused>(os)[0].Reason);
        Assert.Empty(tools.Commands);
    }

    [Fact]
    public async Task ASecondRequestForAPendingResourceReturnsTheSamePlan()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        var first = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));
        var second = await Call(os, NotAPerson("app"), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("pending", second.Reason);
        Assert.Equal(first.Confirmation, second.Confirmation);
        Assert.Single(os.Confirmations.Requested);
    }

    [Fact]
    public async Task TheSeventeenthPendingRestartIsDeclined()
    {
        var names = Enumerable.Range(1, 17).Select(index => $"svc-{index}").ToArray();
        var tools = new FakeTools([.. names, "os"]);
        await using var os = Host(tools);

        foreach (var name in names[..16])
        {
            Assert.Equal("pending", (await Call(os, NotAPerson("agent"), aspire => aspire.Restart(name))).Reason);
        }
        var seventeenth = await Call(os, NotAPerson("agent"), aspire => aspire.Restart(names[16]));

        Assert.Equal("declined", seventeenth.Reason);
        Assert.NotNull(seventeenth.Message);
        Assert.Equal(16, os.Confirmations.Requested.Count);
    }

    [Fact]
    public async Task APendingRestartSurvivesARestartOfTheHostAndCompletesOnApproval()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);
        var pending = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));

        await os.Restart();
        await Answer(os, Person(), pending.Confirmation!, approve: true);

        await Until(() => tools.Commands.Count == 1);
        Assert.Single(Signals<AspireResourceRestarted>(os));
    }

    // The regression for the blocking ask: while a restart waits for the person, the neuron keeps answering.
    [Fact]
    public async Task WhileARestartWaitsTheNeuronKeepsAnswering()
    {
        var tools = new FakeTools("web-searxng", "web-other", "os");
        await using var os = Host(tools);

        var waiting = Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));
        var others = Task.WhenAll(
            waiting,
            Call(os, Person(), async aspire => { await aspire.RecordLogAsync(new AspireLogEntry(os.Clock.GetUtcNow(), "web", "Error", "x")); return true; }),
            Call(os, Person(), async aspire => (await aspire.GetResourcesAsync()).Length > 0),
            Call(os, NotAPerson("app"), async aspire => (await aspire.Restart("web-other")).Reason == "pending"));

        Assert.Same(others, await Task.WhenAny(others, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        Assert.Equal("pending", (await waiting).Reason);
        Assert.Empty(tools.Commands);
    }

    [Fact]
    public async Task ACommandThatFailsReturnsFailedAndPublishesNothing()
    {
        var tools = new FakeTools("web-searxng", "os") { Fails = true };
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart("web-searxng"));

        Assert.False(result.Accepted);
        Assert.Equal("failed", result.Reason);
        Assert.Contains("boom", result.Message, StringComparison.Ordinal);
        Assert.Empty(Signals<AspireResourceRestarted>(os));
    }

    [Fact]
    public async Task AskStillRefusesTheWritingTool()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        await Assert.ThrowsAnyAsync<Exception>(() => Call(os, Person(), aspire => aspire.Ask(AspireMcp.CommandTool, "{\"resourceName\":\"web-searxng\",\"commandName\":\"resource-restart\"}")));
        Assert.Equal(0, tools.Calls);
        Assert.DoesNotContain(AspireMcp.CommandTool, AspireMcp.ReadTools);
    }

    private static TestOS Host(FakeTools tools, ConfirmationPolicy policy = ConfirmationPolicy.DestructiveOnly, string? self = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAspireTools>(tools);
        services.AddSingleton(new AspireInstance(self));
        services.AddSingleton(new ConfirmationPolicies { DefaultPolicy = policy });
        var os = TestOS.Local(o => o.Services = services.BuildServiceProvider());
        os.Register<AspireNeuron>();
        return os;
    }

    private static async Task<T> Call<T>(TestOS os, Caller caller, Func<IAspire, Task<T>> call)
    {
        using var _ = TestEdge.Authority.Enter(caller);
        return await call(os.Get<IAspire>(Aspire));
    }

    // Answers on the real confirmations neuron as the given stamped caller; the Answerer rule decides who may approve.
    private static async Task Answer(TestOS os, Caller caller, string planId, bool approve)
    {
        using (TestEdge.Authority.Enter(caller))
        {
            await os.Get<IConfirmationInbox>(IConfirmationRequests.Of(Brain)).Resolve(planId, approve);
        }
        await os.Idle();
    }

    private static Caller Person() => TestEdge.Authority.Person("local", "local", Brain);

    private static Caller NotAPerson(string kind) => kind switch
    {
        "agent" => TestEdge.Authority.Agent(new Actor.Person("local", "local"), Brain),
        "app" => TestEdge.Authority.App("local/apps/ino", new Actor.Person("local", "local"), Brain),
        _ => TestEdge.Authority.Person("local", "local", Brain, conversation: "chat-1"),
    };

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(done(), "The expected outcome never arrived.");
    }

    private static T[] Signals<T>(TestOS os)
        => [.. os.Journal(Brain).Signals(Aspire).Select(entry => entry.Signal).OfType<T>()];

    // Fills the confirmation quota (ConfirmationState.PendingLimit) of the Aspire neuron as raiser, so its next Request is refused.
    private static async Task<ConfirmationPlan[]> FillTheQuotaOfAspire(TestOS os)
    {
        var plans = Enumerable.Range(0, ConfirmationState.PendingLimit)
            .Select(index => new ConfirmationPlan($"other:{index}", ConfirmationAction.Other, false, "Other?", "Other", "Yes", "No", "other")
                { RequiresPerson = true, Kind = "other", Subject = "other" })
            .ToArray();
        using (TestEdge.Authority.Enter(TestEdge.Authority.OnBehalfOf(new Actor.Person("local", "local"), Brain, via: Aspire)))
        {
            foreach (var plan in plans) { Assert.Null(await os.Get<IConfirmationRequests>(IConfirmationRequests.Of(Brain)).Request(plan)); }
        }
        return plans;
    }

    private static ConfirmationPlan Plan(string id)
        => new(id, ConfirmationAction.Other, false, "Restart?", "Restart", "Restart", "Keep running", "web-searxng")
            { RequiresPerson = true, Kind = AspireNeuron.RestartKind, Subject = "web-searxng" };

    private sealed class FakeTools(params string[] names) : IAspireTools
    {
        public bool Available { get; set; } = true;
        public bool Fails { get; init; }
        public string[] Names { get; set; } = names;
        public int Calls { get; private set; }
        public List<(string, string)> Commands { get; } = [];

        public Task<string> Call(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("");
        }

        public Task<AspireResourceView[]> AllResources(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Names.Select(name => new AspireResourceView(name, "Running", "")).ToArray());
        }

        public Task Command(string resourceName, string commandName, CancellationToken ct)
        {
            Calls++;
            if (Fails) { throw new InvalidOperationException("boom"); }
            Commands.Add((resourceName, commandName));
            return Task.CompletedTask;
        }
    }
}
