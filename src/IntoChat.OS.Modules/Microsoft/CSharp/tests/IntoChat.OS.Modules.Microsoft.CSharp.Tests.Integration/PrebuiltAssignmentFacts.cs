using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A program built ahead of time (MSBuild at OS build) is assigned as assemblies. It is stored, applied and run exactly as a compiled
// one is; what differs is that nothing is compiled and every assembly must hash to its name.
public sealed class PrebuiltAssignmentFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Trait("host", "own")]
    [Fact]
    public async Task APrebuiltProgramRunsThroughTheSandboxAndAnswersRequests()
    {
        await using var rig = await PrebuiltRig.Start(Ct);
        using var caller = rig.Brain.Owner.AsCaller();
        await using var pongs = await rig.Brain.Observe<AppRequested>(rig.App, Ct);
        var built = rig.Build(0);
        var view = await rig.Assign(built);
        Assert.Equal(built.Hash, Assert.Single(view.Behaviors).Hash);
        Assert.True(await rig.Store.Exists(built.Hash, Ct));
        await rig.Attach(Ct);
        await rig.App.Request("add", "5");
        Assert.Equal("5", (await pongs.NextAsync(request => request.Operation == "pong", Ct)).Input);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAssemblyThatDoesNotHashToItsNameRefusesTheWholeProgramBeforeAnythingIsStored()
    {
        await using var rig = await PrebuiltRig.Start(Ct);
        var good = rig.Build(0);
        var other = rig.Build(1);
        var behaviors = PrebuiltRig.One(good);
        behaviors["tampered.cs"] = new([.. other.Assembly, 0], other.Symbols, other.Hash);
        behaviors["claimed.cs"] = new(other.Assembly, other.Symbols, good.Hash);

        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Programs.AssignPrebuilt(rig.AppKey, behaviors, rig.Settings));

        Assert.Empty(rig.Store.Stored);
        Assert.Null(await rig.Programs.Describe(rig.AppKey));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AskingForTheSamePrebuiltProgramAgainAnswersTheAppliedOneAndStoresNothingTwice()
    {
        await using var rig = await PrebuiltRig.Start(Ct);
        var built = rig.Build(0);
        var first = await rig.Assign(built);
        var stored = rig.Store.Stored.Count;

        var again = await rig.Assign(built);

        Assert.Equal(first.Hash, again.Hash);
        Assert.Equal(stored, rig.Store.Stored.Count);
        Assert.Equal(first.Hash, (await rig.Programs.Describe(rig.AppKey))!.Hash);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProgramAssignedAfterAnotherReplacesItAndTheOldAssemblyStaysStoredForRollback()
    {
        await using var rig = await PrebuiltRig.Start(Ct);
        var first = rig.Build(0);
        var second = rig.Build(1);

        await rig.Assign(first);
        var view = await rig.Assign(second);

        Assert.Equal(second.Hash, Assert.Single(view.Behaviors).Hash);
        Assert.True(await rig.Store.Exists(first.Hash, Ct));
    }
}
