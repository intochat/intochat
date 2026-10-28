using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Testing;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A reassign is a drain: the old run stops taking new work, finishes what it is doing, and only then is replaced. If it cannot
// finish within the deadline the update fails and the old run goes on, assigned again, as if nothing had been asked.
public sealed class ReassignDrainFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private static async Task<AppRequested> Next(PlainSignalProbe<AppRequested> requests, string operation, string input)
        => await requests.NextAsync(request => request.Operation == operation && request.Input == input, Ct);

    [Trait("host", "own")]
    [Fact]
    public async Task TheOldRunFinishesItsInFlightRequestBeforeTheNewBuildReplacesIt()
    {
        await using var rig = await PrebuiltRig.Start(Ct);
        using var caller = rig.Brain.Owner.AsCaller();
        await using var requests = await rig.Brain.Observe<AppRequested>(rig.App, Ct);
        var old = rig.Build(0, gated: true);
        var replacement = rig.Build(1000, gated: true);
        await rig.Assign(old);
        await rig.Attach(Ct);
        var id = rig.AppKey + "/add.cs";
        await rig.App.Request("add", "1");
        await Next(requests, "started", "1");

        await rig.Assign(replacement);
        await TestWait.UntilAsync(_ => Task.FromResult(rig.Session!.Behaviors[id].Draining), draining => draining, Within, Ct);

        // The old run is held, not replaced, while its request is in flight.
        Assert.Equal(old.Hash, rig.Session!.Behaviors[id].Hash);
        rig.OpenGate();
        await Next(requests, "pong", "1");
        var view = await rig.Programs.Settled(rig.AppKey, Within);
        Assert.Equal(replacement.Hash, Assert.Single(view!.Behaviors).Hash);
        Assert.Equal("Running", view.Behaviors[0].Status);
        Assert.Equal(replacement.Hash, rig.Session.Behaviors[id].Hash);
        await rig.App.Request("add", "2");
        await Next(requests, "pong", "1002");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WhenTheDeadlinePassesTheOldRunStaysActiveAndTheUpdateFails()
    {
        var time = new FakeTimeProvider();
        var deadline = TimeSpan.FromSeconds(30);
        await using var rig = await PrebuiltRig.Start(Ct);
        using var caller = rig.Brain.Owner.AsCaller();
        await using var requests = await rig.Brain.Observe<AppRequested>(rig.App, Ct);
        var old = rig.Build(0, gated: true);
        var replacement = rig.Build(1000, gated: true);
        await rig.Assign(old);
        await rig.Attach(Ct, time, deadline);
        var id = rig.AppKey + "/add.cs";
        await rig.App.Request("add", "1");
        await Next(requests, "started", "1");
        var oldRun = rig.Session!.Behaviors[id];

        await rig.Assign(replacement);
        await TestWait.UntilAsync(_ => Task.FromResult(oldRun.Draining), draining => draining, Within, Ct);
        time.Advance(deadline);
        var view = await rig.Programs.Settled(rig.AppKey, Within);

        // The assigner sees that the update did not happen, and which build is still the program.
        var behavior = Assert.Single(view!.Behaviors);
        Assert.Equal("UpdateFailed", behavior.Status);
        Assert.Equal(old.Hash, behavior.Hash);
        Assert.Contains("in flight", behavior.Error, StringComparison.Ordinal);
        // The old run was neither stopped nor replaced, and the failure is in its log.
        Assert.Same(oldRun, rig.Session.Behaviors[id]);
        Assert.False(oldRun.Draining);
        Assert.Contains("in flight", oldRun.Tail(10), StringComparison.Ordinal);
        // It finishes the request it had, and takes the next ones: the signals it is owed were restored, none was lost.
        await rig.App.Request("add", "2");
        rig.OpenGate();
        await Next(requests, "pong", "1");
        await Next(requests, "pong", "2");
        Assert.Equal(old.Hash, (await rig.Programs.Describe(rig.AppKey))!.Behaviors[0].Hash);
    }
}
