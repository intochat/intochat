using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Card;
using IntoChat.OS.Modules.Flutter.Collection;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class FriendsReplayFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedDestinationDoesNotBlockReplayOrTheNextRequest(bool race)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new PausePublication();
        await using var brain = await Start(ct, gate);
        var admin = await brain.PersonAsync("admin", ct);
        string removed;
        using (admin.AsCaller())
        { removed = await brain.Get<ICircleFactory>(ICircleFactory.Key(admin.BrainId)).Create("removed"); }
        using (admin.AsCallerIn(removed)) { await brain.Get<ICircle>(ICircle.Key(removed)).Invite(brain.Owner.AccountId); }
        using (brain.Owner.AsCallerIn(removed)) { await brain.Get<ICircle>(ICircle.Key(removed)).Accept(); }
        var retainedCircle = await Circle(brain, "retained");
        var app = await App(brain);
        await Handle(brain, await Request(brain, app, ct));
        var replay = await Replay(brain, app, ct);
        if (race)
        {
            gate.Target = ICircleChat.Key(removed);
            var handling = Handle(brain, replay);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            try
            {
                using (admin.AsCallerIn(removed)) { await brain.Get<ICircle>(ICircle.Key(removed)).Remove(brain.Owner.AccountId); }
            }
            finally { gate.Release.TrySetResult(); }
            await handling;
            Assert.True(gate.DeniedByRealGuard);
        }
        else
        {
            using (admin.AsCallerIn(removed)) { await brain.Get<ICircle>(ICircle.Key(removed)).Remove(brain.Owner.AccountId); }
            await Handle(brain, replay);
        }
        Assert.Single(await Lines(brain, retainedCircle));
        // The reaction can continue to its next request rather than restarting on the old one forever.
        await Handle(brain, await Request(brain, app, ct));
        Assert.Equal(2, (await Lines(brain, retainedCircle)).Length);
        using (admin.AsCallerIn(removed))
        { Assert.Single(await brain.Get<ICircleChat>(ICircleChat.Key(removed)).Read(0)); }
    }

    [Fact]
    public async Task AuthorizationFailureWhileStillAMemberIsNotHidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct, new DenyPublication());
        var app = await App(brain);
        await Circle(brain, "eligible");
        var request = await Request(brain, app, ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Handle(brain, request));
    }

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
        // The plain app keeps its requests in its state (06-08); the host-only migration contract reads them.
        var retention = brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IAppMigration>(IApp.Key(brain.Owner.BrainId, "friends"));
        return Assert.Single(await retention.RetainedRequests()).Request;
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

    private sealed class PausePublication : IIncomingGrainCallFilter
    {
        public string? Target { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DeniedByRealGuard { get; private set; }
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.InterfaceMethod.Name == nameof(ICircleChat.PublishPost) && context.TargetId.Key.ToString() == Target)
            {
                Target = null;
                Entered.TrySetResult();
                await Release.Task;
                try { await context.Invoke(); }
                catch (UnauthorizedAccessException) { DeniedByRealGuard = true; throw; }
            }
            else { await context.Invoke(); }
        }
    }

    private sealed class DenyPublication : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context)
            => context.InterfaceMethod.Name == nameof(ICircleChat.PublishPost)
                ? Task.FromException(new UnauthorizedAccessException("Unrelated authorization refusal")) : context.Invoke();
    }

    private static Task<ITestBrain> Start(CancellationToken ct, IIncomingGrainCallFilter? failure = null)
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
