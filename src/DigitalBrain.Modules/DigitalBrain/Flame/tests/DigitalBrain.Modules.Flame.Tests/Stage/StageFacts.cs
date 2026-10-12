using DigitalBrain.Flame;
using DigitalBrain.Flame.Stage;
using DigitalBrain.Flame.Stage.Signals;
using DigitalBrain.Framework;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Stage;

public sealed class StageFacts
{
    private const string Key = "owner/stage/abc123";   // in production: the person's shell scope digest

    [Fact]
    public async Task GoPushesAndPublishesTheTop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var stage = brain.Get<IStage>(Key);
        await stage.SetHome("owner/apps/ino/scenes/home");
        await using var changes = await brain.Observe<StageChanged>(stage, ct);
        await stage.Go("owner/apps/ino/scenes/home", "owner/apps/files/surface");
        var changed = await changes.NextAsync(ct: ct);
        Assert.Equal("owner/apps/ino/scenes/home", changed.Top);
        var state = await stage.Read();
        Assert.Equal(2, state.Stack.Length);
        Assert.Equal("owner/apps/files/surface", state.Stack[^1].DockSurfaceKey);
    }

    [Fact]
    public async Task BackPopsToThePreviousEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var stage = brain.Get<IStage>(Key);
        await stage.SetHome("owner/apps/ino/scenes/home");
        await stage.Go("owner/apps/brainview/scenes/brain");
        await stage.Back();
        var state = await stage.Read();
        Assert.Single(state.Stack);
        Assert.Equal("owner/apps/ino/scenes/home", state.Stack[0].SceneKey);
    }

    [Fact]
    public async Task BackOnHomeStaysOnHomeAndPublishesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var stage = brain.Get<IStage>(Key);
        await stage.SetHome("owner/apps/ino/scenes/home");
        var before = (await stage.Read()).Revision;
        await using var changes = await brain.Observe<StageChanged>(stage, ct);
        await stage.Back();
        Assert.Equal(before, (await stage.Read()).Revision);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var next = changes.NextAsync(ct: pending.Token);
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(300), ct)));
        await pending.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task HomeOnBareHomeSavesNothingAndPublishesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var stage = brain.Get<IStage>(Key);
        await stage.SetHome("owner/apps/ino/scenes/home");
        var before = (await stage.Read()).Revision;
        await using var changes = await brain.Observe<StageChanged>(stage, ct);
        await stage.Home();
        Assert.Equal(before, (await stage.Read()).Revision);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var next = changes.NextAsync(ct: pending.Token);
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(300), ct)));
        await pending.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task HomeClearsTheStackToTheHomeScene()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var stage = brain.Get<IStage>(Key);
        await stage.SetHome("owner/apps/ino/scenes/home");
        await stage.Go("a/apps/x/scenes/1"); await stage.Go("a/apps/x/scenes/2"); await stage.Go("a/apps/x/scenes/3");
        await stage.Home();
        var state = await stage.Read();
        Assert.Single(state.Stack);
        Assert.Equal("owner/apps/ino/scenes/home", state.Stack[0].SceneKey);
    }

    [Fact]
    public async Task GoBeforeSetHomeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IStage>(Key).Go("a/apps/x/scenes/1"));
    }

    [Fact]
    public async Task AnotherPersonsSessionIsRefusedTheStage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var owner = brain.Owner;
        var scope = StageNeuron.ScopeOf(new CallerContext { PrincipalId = owner.PrincipalId, AccountId = owner.AccountId, BrainId = owner.BrainId, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        var stage = brain.Get<IStage>(scope);
        using (owner.AsCaller()) { await stage.SetHome("owner/apps/ino/scenes/home"); }
        var carol = await brain.PersonAsync("carol", ct);
        using (carol.AsCallerIn(owner.BrainId))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(stage.Read);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stage.Go("owner/apps/x/scenes/1"));
        }
        using (owner.AsCaller()) { Assert.Single((await stage.Read()).Stack); }
    }

    [Fact]
    public async Task TheOwnersStageIsAddressableAsBrainSlashStageSlashOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var stage = brain.Get<IStage>("owner/stage/owner");
        using (brain.Owner.AsCaller())
        {
            await stage.SetHome("owner/apps/ino/scenes/home");
            await stage.Go("owner/apps/brainview/scenes/brain");
            Assert.Equal(2, (await stage.Read()).Stack.Length);
        }
        using (brain.Owner.AsCallerIn("bob"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(stage.Read);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(stage.Back);
        }
    }

    [Fact]
    public async Task ANonOwnerAccountOfTheSameBrainIsRefusedTheOwnerAlias()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var owner = brain.Owner;
        var stage = brain.Get<IStage>("owner/stage/owner");
        using (owner.AsCaller()) { await stage.SetHome("owner/apps/ino/scenes/home"); }
        var carol = await brain.PersonAsync("carol", ct);
        using (carol.AsCallerIn(owner.BrainId))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(stage.Read);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stage.Go("owner/apps/x/scenes/1"));
        }
        using (owner.AsCaller()) { Assert.Single((await stage.Read()).Stack); }
    }
}
