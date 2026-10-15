using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Routing;
using DigitalBrain.Framework;
using DigitalBrain.Modules.AI.Tests.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class ContinuationGateFacts
{
    [Fact]
    public async Task AskDoesNotReturnUntilTheRouteContinuationIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new BlockingContinuation();
        await using var brain = await TestBrain.Create().WithModule<AIModule>()
            .ConfigureServices(services => services.AddSingleton<IWorkContinuation>(gate))
            .StartAsync(ct);
        var asking = brain.Get<IRouter>("owner").Ask(new RouteRequest(Guid.NewGuid(), "Show active leads"), ct);

        await gate.Entered.Task.WaitAsync(ct);
        Assert.False(asking.IsCompleted);
        gate.Unblock();
        await asking.WaitAsync(ct);
        await gate.Released.Task.WaitAsync(ct);
        Assert.StartsWith("route:", gate.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartHoldsTheContinuationUntilTheTurnHasSignaled()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new BlockingContinuation();
        await using var brain = await AgentFacts.Builder(StubChatClient.Replying("hello"))
            .ConfigureServices(services => services.AddSingleton<IWorkContinuation>(gate))
            .StartAsync(ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        var starting = agent.Start(AgentTurn.Text("hi"));

        await gate.Entered.Task.WaitAsync(ct);
        Assert.False(starting.IsCompleted);
        gate.Unblock();
        await starting.WaitAsync(ct);
        await gate.Released.Task.WaitAsync(ct);
        Assert.StartsWith("agent:", gate.Token, StringComparison.Ordinal);
    }

    private sealed class BlockingContinuation : IWorkContinuation
    {
        private readonly TaskCompletionSource _block = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Token { get; private set; }

        public Task Acquire(string brainScope, string token)
        {
            Token = token;
            Entered.TrySetResult();
            return _block.Task;
        }

        public void Unblock() => _block.TrySetResult();

        public Task Release(string brainScope, string token)
        {
            Released.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
