using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Flame;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class FriendsReplayFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedReplayNeverAddsANewCircle(bool initialCircle)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var app = await App(brain);
        var first = initialCircle ? await Circle(brain, "first") : null;
        var request = await Request(brain, app, ct);
        await Handle(brain, request);
        var added = await Circle(brain, "added");
        // Read the actual durable backlog used to seed a restarted reaction.
        var retained = await Replay(brain, app, ct);
        Assert.Equal(request.RequestId, retained.RequestId);
        await Handle(brain, retained);
        Assert.Empty(await Lines(brain, added));
        if (first is not null) { Assert.Single(await Lines(brain, first)); }
        Assert.Single((await brain.Get<ICollectionView>(brain.Owner.Key("posts")).Read()).Definition.Items);
    }

    [Fact]
    public async Task PartialFanoutRetriesOnlyItsInitialAudienceWithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var failure = new FailSecondPublication();
        await using var brain = await Start(ct, failure);
        var app = await App(brain);
        var first = await Circle(brain, "first");
        var second = await Circle(brain, "second");
        var request = await Request(brain, app, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handle(brain, request));
        Assert.Equal(1, (await Lines(brain, first)).Length + (await Lines(brain, second)).Length);
        var added = await Circle(brain, "added");
        await Handle(brain, await Replay(brain, app, ct));
        Assert.Single(await Lines(brain, first));
        Assert.Single(await Lines(brain, second));
        Assert.Empty(await Lines(brain, added));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPostWithoutAnAudienceCannotGainDestinations(bool indexedOnly)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var app = await App(brain);
        var request = await Request(brain, app, ct);
        var key = brain.Owner.Key("posts/" + request.RequestId.ToString("N"));
        if (indexedOnly)
        { await brain.Get<ICollectionView>(brain.Owner.Key("posts")).Set(new([new(key, "Old", "post")]), 0); }
        else { await brain.Get<ICard>(key).Set("Old", "Old body"); }
        var added = await Circle(brain, "added");
        await Handle(brain, request);
        Assert.Empty(await Lines(brain, added));
    }

    private static async Task<IApp> App(ITestBrain brain)
    {
        var app = brain.Get<IApp>(IApp.Key(brain.Owner.BrainId, "friends"));
        await app.Create(new("Friends", "", [new("post", "Post")], [], []), "spec");
        return app;
    }

    private static async Task<AppRequested> Request(ITestBrain brain, IApp app, CancellationToken ct)
    {
        await using var requests = await brain.Observe<AppRequested>(app, ct);
        await app.Request("post", "Original audience only");
        return await requests.NextAsync(ct: ct);
    }

    private static async Task<AppRequested> Replay(ITestBrain brain, IApp app, CancellationToken ct)
    {
        await brain.DeactivateAsync(app, ct);
        var retention = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IRetainsSignals>(app.GetGrainId());
        return Assert.IsType<AppRequested>(Assert.Single(await retention.Retained(nameof(AppRequested), 0)).Signal);
    }

    private static async Task Handle(ITestBrain brain, AppRequested request)
    {
        var key = IApp.Key(brain.Owner.BrainId, "friends");
        using (brain.Owner.AsApp(key))
        { await Friends.Post(brain, brain.Owner.BrainId, request.RequestId, request.Input); }
    }

    private static async Task<string> Circle(ITestBrain brain, string name)
    {
        using (brain.Owner.AsCaller())
        { return await brain.Get<ICircleFactory>(ICircleFactory.Key(brain.Owner.BrainId)).Create(name); }
    }

    private static async Task<CircleLine[]> Lines(ITestBrain brain, string circle)
    {
        using (brain.Owner.AsCallerIn(circle))
        { return await brain.Get<ICircleChat>(ICircleChat.Key(circle)).Read(0); }
    }

    // Fails one transport turn before it reaches the real neuron, after another circle committed.
    private sealed class FailSecondPublication : IIncomingGrainCallFilter
    {
        private int _calls;
        public Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.InterfaceMethod.Name == nameof(ICircleChat.PublishPost)
                && Interlocked.Increment(ref _calls) == 2) { throw new InvalidOperationException("Interrupted fanout"); }
            return context.Invoke();
        }
    }

    private static Task<ITestBrain> Start(CancellationToken ct, FailSecondPublication? failure = null)
        => TestBrain.Create().WithModule<AppsModule, AppsOptions>(o => o.Features = SolutionProjects.Features)
            .WithModule<CirclesModule>().WithModule<FlutterModule>().WithModule<FlameModule>()
            .ConfigureServices(s =>
            {
                s.RemoveAll<IAppRuntime>(); s.AddSingleton<IAppRuntime, NoRuntime>();
                if (failure is not null) { s.AddSingleton<IIncomingGrainCallFilter>(failure); }
            }).StartAsync(ct);

    private sealed class NoRuntime : IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources,
            Dictionary<string, string> configuration, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task Retire(string appKey, CancellationToken ct) => Task.CompletedTask;
    }
}
