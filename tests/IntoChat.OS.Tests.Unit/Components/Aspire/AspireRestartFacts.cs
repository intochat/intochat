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

    // The live list exactly as Aspire's MCP list_resources returned it (CLI 17.0.0, hosting 17.1.0-preview): replicas and persistent
    // containers carry an instance suffix the dashboard does not show.
    private static readonly string[] LiveNames =
    [
        "Modules-intochat-os-master-key", "Modules-searxng-secret", "ai", "ai-ollama-a265990c", "ai-ollama-openwebui-a265990c", "aspire",
        "devices", "github", "intochat", "intochat-os-bootstrap-password", "mcp", "orleans", "os-gateway-bcrfeyre", "os-gdnnbbha", "web",
        "web-searxng-a265990c",
    ];

    [Theory]
    [InlineData("os-gdnnbbha", "person")]
    [InlineData("os-gateway-bcrfeyre", "person")]
    [InlineData("os", "person")]
    [InlineData("os-gateway", "person")]
    [InlineData("os-gdnnbbha", "agent")]
    [InlineData("os-gateway-bcrfeyre", "agent")]
    [InlineData("os", "agent")]
    [InlineData("os-gateway", "agent")]
    public async Task TheKernelUnderItsLiveNameIsRefused(string name, string caller)
    {
        var tools = new FakeTools(LiveNames);
        await using var os = Host(tools, self: "os");

        var result = await Call(os, caller == "person" ? Person() : NotAPerson("agent"), aspire => aspire.Restart(name));

        Assert.Equal("self", result.Reason);
        Assert.Empty(tools.Commands);
        Assert.Empty(os.Confirmations.Requested);
        Assert.Empty(Signals<AspireResourceRestartRequested>(os));
    }

    [Theory]
    [InlineData("OperatingSystem-a-abcdefgh")]
    [InlineData("OperatingSystem-front-abcdefgh")]
    [InlineData("OperatingSystem-a")]
    public async Task APublishedKernelUnderItsLiveNameIsRefused(string name)
    {
        var tools = new FakeTools("web-searxng-a265990c", "OperatingSystem-a-abcdefgh", "OperatingSystem-front-abcdefgh");
        await using var os = Host(tools, self: "OperatingSystem-a");

        var result = await Call(os, NotAPerson("agent"), aspire => aspire.Restart(name));

        Assert.Equal("self", result.Reason);
        Assert.Empty(os.Confirmations.Requested);
    }

    // Assumed shapes: the rebuilder replica carries its own suffix, or sits beside a suffixed kernel.
    [Theory]
    [InlineData("os-rebuilder-abcdefgh")]
    [InlineData("os-gdnnbbha-rebuilder")]
    [InlineData("os-rebuilder")]
    public async Task TheRebuilderOfASuffixedKernelIsRefused(string name)
    {
        var tools = new FakeTools([.. LiveNames, "os-rebuilder-abcdefgh", "os-gdnnbbha-rebuilder"]);
        await using var os = Host(tools, self: "os");

        var result = await Call(os, NotAPerson("agent"), aspire => aspire.Restart(name));

        Assert.Equal("self", result.Reason);
        Assert.Empty(os.Confirmations.Requested);
    }

    // A display name given by list_resources itself counts for the self rule too.
    [Fact]
    public async Task AKernelNamedOnlyByItsDisplayNameFieldIsRefused()
    {
        var tools = new FakeTools("web-searxng-a265990c", "kernel-x1") { Display = { ["kernel-x1"] = "os" } };
        await using var os = Host(tools, self: "os");

        var result = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("kernel-x1"));

        Assert.Equal("self", result.Reason);
        Assert.Empty(os.Confirmations.Requested);
    }

    [Fact]
    public async Task ADashboardNameResolvesToTheOneLiveResource()
    {
        var tools = new FakeTools(LiveNames);
        await using var os = Host(tools, self: "os");

        var result = await Call(os, Person(), aspire => aspire.Restart("web-searxng"));

        Assert.True(result.Accepted);
        Assert.Equal("web-searxng-a265990c", result.Resource);
        Assert.Equal([("web-searxng-a265990c", AspireMcp.RestartCommand)], tools.Commands);
        Assert.Equal("web-searxng-a265990c", Assert.Single(Signals<AspireResourceRestarted>(os)).Resource);
    }

    [Fact]
    public async Task ADisplayNameFieldResolvesToItsLiveResource()
    {
        var tools = new FakeTools("search-x1", "os") { Display = { ["search-x1"] = "search" } };
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart("search"));

        Assert.Equal([("search-x1", AspireMcp.RestartCommand)], tools.Commands);
        Assert.Equal("search-x1", result.Resource);
    }

    [Fact]
    public async Task TheExactLiveNameStillWorks()
    {
        var tools = new FakeTools(LiveNames);
        await using var os = Host(tools, self: "os");

        var result = await Call(os, Person(), aspire => aspire.Restart("web-searxng-a265990c"));

        Assert.True(result.Accepted);
        Assert.Equal([("web-searxng-a265990c", AspireMcp.RestartCommand)], tools.Commands);
    }

    [Fact]
    public async Task ADisplayNameSharedByTwoReplicasIsUnknown()
    {
        var tools = new FakeTools("os", "web", "worker-aaaaaaaa", "worker-bbbbbbbb");
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart("worker"));

        Assert.Equal("unknown", result.Reason);
        Assert.Equal(["worker-aaaaaaaa", "worker-bbbbbbbb"], result.Known![..2]);
        Assert.Empty(tools.Commands);
    }

    [Fact]
    public async Task ARequestByDisplayNameAndThenByExactNameIsOneRequest()
    {
        var tools = new FakeTools(LiveNames);
        await using var os = Host(tools, self: "os");

        var first = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));
        var second = await Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng-a265990c"));
        var third = await Call(os, NotAPerson("app"), aspire => aspire.Restart("web-searxng"));

        Assert.Equal("pending", first.Reason);
        Assert.Equal("web-searxng-a265990c", first.Resource);
        Assert.Equal(first.Confirmation, second.Confirmation);
        Assert.Equal(first.Confirmation, third.Confirmation);
        Assert.Single(os.Confirmations.Requested);
        Assert.Equal("web-searxng-a265990c", Assert.Single(Signals<AspireResourceRestartRequested>(os)).Resource);
    }

    // An entry for the kernel stored before this fix (the old rule let os-gdnnbbha through to a card) never runs when approved.
    [Fact]
    public async Task SelfIsRecheckedWhenTheAnswerArrives()
    {
        var tools = new FakeTools(LiveNames);
        await using var os = Host(tools, self: "os");
        var now = os.Clock.GetUtcNow().ToString("O");
        var planId = "aspire.resource.restart:os-gdnnbbha:old";
        os.SeedStoredState(Aspire, "{\"resources\":[],\"structuredLogs\":[],\"traces\":[],\"pendingRestarts\":[{\"planId\":\"" + planId + "\",\"resource\":\"os-gdnnbbha\",\"requestedAt\":\"" + now + "\"}]}");
        using (TestEdge.Authority.Enter(TestEdge.Authority.OnBehalfOf(new Actor.Person("local", "local"), Brain, via: Aspire)))
        {
            Assert.Null(await os.Get<IConfirmationRequests>(IConfirmationRequests.Of(Brain)).Request(
                new ConfirmationPlan(planId, ConfirmationAction.Other, false, "Restart os-gdnnbbha?", "Restart", "Restart", "Keep running", "os-gdnnbbha")
                    { RequiresPerson = true, Kind = AspireNeuron.RestartKind, Subject = "os-gdnnbbha" }));
        }
        await Call(os, Person(), async aspire => (await aspire.GetResourcesAsync()).Length);

        await Answer(os, Person(), planId, approve: true);

        await Until(() => Signals<AspireResourceRestartRefused>(os).Length == 1);
        Assert.Equal([new AspireResourceRestartRefused("os-gdnnbbha", "self")], Signals<AspireResourceRestartRefused>(os));
        Assert.Empty(tools.Commands);
    }

    // Fail closed whatever suffix format Aspire uses: a name that is, or begins with, a kernel base and a separator is the kernel.
    [Theory]
    [InlineData("os_gdnnbbha", "person")]
    [InlineData("os-Gdnnbbh1x", "person")]
    [InlineData("os-gdnnbbh", "person")]
    [InlineData("os.gdnnbbha", "person")]
    [InlineData("os-gdnn-bbha", "person")]
    [InlineData("OS-gdnnbbha", "person")]
    [InlineData("os-gateway_x1", "person")]
    [InlineData("OperatingSystem_a", "person")]
    [InlineData("OperatingSystem-a.b", "person")]
    [InlineData("os_gdnnbbha", "agent")]
    [InlineData("os-Gdnnbbh1x", "agent")]
    [InlineData("os-gdnnbbh", "agent")]
    [InlineData("os.gdnnbbha", "agent")]
    [InlineData("os-gdnn-bbha", "agent")]
    [InlineData("OS-gdnnbbha", "agent")]
    [InlineData("os-gateway_x1", "agent")]
    [InlineData("OperatingSystem_a", "agent")]
    [InlineData("OperatingSystem-a.b", "agent")]
    public async Task TheKernelIsRefusedWhateverSuffixAspireUses(string name, string caller)
    {
        var tools = new FakeTools("web-searxng-a265990c", name);
        await using var os = Host(tools, self: "os");
        await Call(os, Person(), async aspire => (await aspire.GetResourcesAsync()).Length);
        var saves = os.Journal(Brain).Saves;

        var result = await Call(os, caller == "person" ? Person() : NotAPerson("agent"), aspire => aspire.Restart(name));

        Assert.Equal("self", result.Reason);
        Assert.Empty(tools.Commands);
        Assert.Empty(os.Confirmations.Requested);
        Assert.Equal(saves, os.Journal(Brain).Saves); // nothing stored: no pending entry, no signal
    }

    [Theory]
    [InlineData("kernel-7", true)]
    [InlineData("kernel-7-abcdefgh", true)]
    [InlineData("kernel-7_x", true)]
    [InlineData("kernel-70", false)]
    [InlineData("kernel", false)]
    public async Task AnInstanceNameThatIsNotAKnownShapeStillProtectsItsOwnResources(string name, bool self)
    {
        var tools = new FakeTools("os", name);
        await using var os = Host(tools, self: "kernel-7");

        var result = await Call(os, Person(), aspire => aspire.Restart(name));

        Assert.Equal(self ? "self" : null, result.Reason);
        Assert.Equal(self ? 0 : 1, tools.Commands.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnEmptyInstanceNameRefusesNothingExtra(string? self)
    {
        var tools = new FakeTools("web-searxng-a265990c", "ai", "intochat", "os-gdnnbbha");
        await using var os = Host(tools, self: self);

        foreach (var name in (string[])["web-searxng-a265990c", "ai", "intochat"])
        {
            Assert.True((await Call(os, Person(), aspire => aspire.Restart(name))).Accepted);
        }
        Assert.Equal("self", (await Call(os, Person(), aspire => aspire.Restart("os-gdnnbbha"))).Reason);
        Assert.Equal(3, tools.Commands.Count);
    }

    [Theory]
    [InlineData("oscar")]
    [InlineData("osmosis-cache")]
    [InlineData("OperatingSystems")]
    public async Task NamesThatOnlyBeginWithTheLettersAreNotRefused(string name)
    {
        var tools = new FakeTools("os", name);
        await using var os = Host(tools, self: "os");

        Assert.True((await Call(os, Person(), aspire => aspire.Restart(name))).Accepted);
    }

    [Fact]
    // "password" is 8 lowercase letters, so the parameter intochat-os-bootstrap-password derives intochat-os-bootstrap: the coincidence
    // case DisplayNameOf accepts. It is not self (it does not begin with a kernel base).
    public void TheLiveNameShapeAspireUsedOn20261123IsRecognised()
    {
        Assert.Equal(["os-gateway-bcrfeyre", "os-gdnnbbha"], LiveNames.Where(name => KernelNames.IsSelfLive(name, null, "os")));
        Assert.Equal(
            ["Modules-intochat-os-master-key", "Modules-searxng-secret", "ai", "ai-ollama", "ai-ollama-openwebui", "aspire", "devices", "github",
             "intochat", "intochat-os-bootstrap", "mcp", "orleans", "os-gateway", "os", "web", "web-searxng"],
            LiveNames.Select(AspireMcp.DisplayNameOf));
    }

    [Theory]
    [InlineData("os-gdnnbbha", "os")]
    [InlineData("os-gateway-bcrfeyre", "os-gateway")]
    [InlineData("web-searxng-a265990c", "web-searxng")]
    [InlineData("ai-ollama-openwebui-a265990c", "ai-ollama-openwebui")]
    [InlineData("intochat", "intochat")]
    [InlineData("Modules-intochat-os-master-key", "Modules-intochat-os-master-key")]
    [InlineData("Modules-searxng-secret", "Modules-searxng-secret")]
    [InlineData("worker-ABCDEFGH", "worker-ABCDEFGH")]
    [InlineData("abcdefgh", "abcdefgh")]
    [InlineData("-abcdefgh", "-abcdefgh")]
    [InlineData("my-database", "my")]
    public void TheDisplayNameDropsAnEightCharacterInstanceSuffix(string live, string display)
        => Assert.Equal(display, AspireMcp.DisplayNameOf(live));

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
        public Dictionary<string, string> Display { get; } = [];
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
            return Task.FromResult(Names.Select(name => new AspireResourceView(name, "Running", "", Display.GetValueOrDefault(name))).ToArray());
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
