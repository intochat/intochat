using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Testing;
using IntoChat.Aspire;
using Microsoft.Extensions.DependencyInjection;
using TestEdge = IntoChat.OS.Tests.TestEdge;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

// IAspire.Restart: a person restarts at once; anyone else waits for the person's yes. The kernel, unknown names and a missing AppHost are refused.
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
        Assert.Equal([new AspireResourceRestarted("web-searxng", os.Clock.GetUtcNow())], Restarted(os));
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
    public async Task TheKernelItselfIsRefused(string kernel)
    {
        var tools = new FakeTools("web-searxng", kernel);
        await using var os = Host(tools);

        var result = await Call(os, Person(), aspire => aspire.Restart(kernel));

        Assert.False(result.Accepted);
        Assert.Equal("self", result.Reason);
        Assert.Empty(tools.Commands);
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
    public async Task ACallerThatIsNotAPersonNeedsThePersonsConfirmation(string kind)
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        var approved = Call(os, NotAPerson(kind), aspire => aspire.Restart("web-searxng"));
        var plan = await Asked(os);
        Assert.Empty(tools.Commands);
        await os.Confirmations.Approve(plan.PlanId);
        Assert.True((await approved).Accepted);
        Assert.Single(tools.Commands);
        Assert.Single(Restarted(os));

        var declined = Call(os, NotAPerson(kind), aspire => aspire.Restart("web-searxng"));
        await os.Confirmations.Deny((await Asked(os)).PlanId);
        Assert.Equal("declined", (await declined).Reason);
        Assert.Single(tools.Commands);
        Assert.Single(Restarted(os));
    }

    [Fact]
    public async Task AnAgentCannotApproveItsOwnRestart()
    {
        var tools = new FakeTools("web-searxng", "os");
        await using var os = Host(tools);

        var restart = Call(os, NotAPerson("agent"), aspire => aspire.Restart("web-searxng"));
        var plan = await Asked(os);
        os.Confirmations.Current = ConfirmationPrincipal.Agent("ino");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => os.Confirmations.Approve(plan.PlanId));
        Assert.False(restart.IsCompleted);
        Assert.Contains(os.Confirmations.Waiting, waiting => waiting.PlanId == plan.PlanId);
        Assert.Empty(tools.Commands);

        os.Confirmations.Current = ConfirmationPrincipal.Person("local");
        await os.Confirmations.Deny(plan.PlanId);
        Assert.Equal("declined", (await restart).Reason);
        Assert.Empty(tools.Commands);
        Assert.Empty(Restarted(os));
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
        Assert.Empty(Restarted(os));
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

    private static TestOS Host(FakeTools tools)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAspireTools>(tools);
        var os = TestOS.Local(o => o.Services = services.BuildServiceProvider());
        os.Register<AspireNeuron>();
        return os;
    }

    private static async Task<T> Call<T>(TestOS os, Caller caller, Func<IAspire, Task<T>> call)
    {
        using var _ = TestEdge.Authority.Enter(caller);
        return await call(os.Get<IAspire>(Aspire));
    }

    private static Caller Person() => TestEdge.Authority.Person("local", "local", Brain);

    private static Caller NotAPerson(string kind) => kind switch
    {
        "agent" => TestEdge.Authority.Agent(new Actor.Person("local", "local"), Brain),
        "app" => TestEdge.Authority.App("local/apps/ino", new Actor.Person("local", "local"), Brain),
        _ => TestEdge.Authority.Person("local", "local", Brain, conversation: "chat-1"),
    };

    private static async Task<ConfirmationRequested> Asked(TestOS os)
    {
        for (var i = 0; i < 400; i++)
        {
            if (os.Confirmations.Waiting.FirstOrDefault() is { } plan) { return plan; }
            await Task.Delay(10);
        }
        throw new TimeoutException("No confirmation was asked.");
    }

    private static AspireResourceRestarted[] Restarted(TestOS os)
        => [.. os.Journal(Brain).Signals(Aspire).Select(entry => entry.Signal).OfType<AspireResourceRestarted>()];

    private sealed class FakeTools(params string[] names) : IAspireTools
    {
        public bool Available { get; init; } = true;
        public bool Fails { get; init; }
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
            return Task.FromResult(names.Select(name => new AspireResourceView(name, "Running", "")).ToArray());
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
