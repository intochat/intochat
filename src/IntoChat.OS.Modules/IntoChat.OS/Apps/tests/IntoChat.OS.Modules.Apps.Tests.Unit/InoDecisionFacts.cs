using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Decision;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InoDecisionFacts
{
    private sealed class MockDecisionMaker(string returnChoice, double returnConfidence) : IDecisionMaker
    {
        public DecisionContext? LastContext { get; private set; }

        public Task<DecisionResult> DecideAsync(DecisionContext context, CancellationToken ct = default)
        {
            LastContext = context;
            return Task.FromResult(new DecisionResult(returnChoice, returnConfidence, DecisionMode.Reactive));
        }

        public Task<FastDecision> DecideReactiveAsync(ReactivePrompt prompt, CancellationToken ct = default)
            => Task.FromResult(new FastDecision(returnChoice, returnConfidence, new Dictionary<string, double> { [returnChoice] = returnConfidence }));

        public Task<DeliberateDecision> DecideDeliberateAsync(DeliberateGoal goal, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task InoAppRoutesViaDecisionMakerWhenConfigured()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var mock = new MockDecisionMaker("photocraft", 0.95);
        ino.App.DecisionMaker = mock;

        await ino.Request("send", new InoSend("Make a vintage poster with sunset"));

        Assert.NotNull(mock.LastContext);
        Assert.Equal("Make a vintage poster with sunset", mock.LastContext.Intent);
        Assert.Contains(ino.App.State.Turns, t => t.RouteHint.Contains("photocraft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InoAppFallsBackToPatternMatchingWhenDecisionMakerNull()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        ino.App.DecisionMaker = null;

        await ino.Request("send", new InoSend("Remove background from photo"));

        Assert.Contains(ino.App.State.Turns, t => t.RouteHint.Contains("photocraft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InoAppFallsBackForFilmCraftWhenDecisionMakerNull()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        ino.App.DecisionMaker = null;

        await ino.Request("send", new InoSend("Trim video at 5 seconds"));

        Assert.Contains(ino.App.State.Turns, t => t.RouteHint.Contains("filmcraft", StringComparison.OrdinalIgnoreCase));
    }
}
