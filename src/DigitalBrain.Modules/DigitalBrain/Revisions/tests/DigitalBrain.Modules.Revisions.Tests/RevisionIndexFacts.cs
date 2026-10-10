using DigitalBrain.Revisions;
using DigitalBrain.Revisions.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Revisions.Tests;

public sealed class RevisionIndexFacts
{
    private static readonly VerificationPlan Plan = new([], [], [], TimeSpan.FromMinutes(1));

    private static Task<ModuleBrain> StartAsync(FakeLauncher launcher, CancellationToken ct)
        => ModuleTest.Create().WithModule<RevisionsModule>()
            .ConfigureSilo(silo => { silo.Services.AddSingleton<ILauncherClient>(launcher); silo.Services.AddSingleton<IRevisionVerifier>(new FakeVerifier()); })
            .StartAsync(ct);

    [Fact]
    public async Task ProposingCommitsThroughTheLauncherAndStartsTheRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var launcher = new FakeLauncher();
        await using var brain = await StartAsync(launcher, ct);
        var index = brain.Get<IRevisions>("owner");
        var revision = brain.Get<IRevision>(IRevision.Key("owner", "sha0001"));
        await using var changes = await brain.Observe<RevisionChanged>(revision, ct);
        var sha = await index.Propose(new("os-runtime", "Calculator: add percent", new() { ["Calculator/app.json"] = "{}" }, Plan));
        Assert.Equal("sha0001", sha);
        while ((await revision.Read()).Stage != RevisionStage.Healthy) { await changes.NextAsync(ct: ct); }
        Assert.Equal(["propose sha0001", "start sha0001", "healthy sha0001"], launcher.Calls);
        Assert.Equal(["owner/revisions/" + sha], await new DigitalBrain.Framework.Kernel(brain.Grains).InstancesIn("owner", "revisions.revision", ct).Select(instance => instance.Key).ToArrayAsync(ct));
        Assert.Equal(sha, await index.Live());
    }

    [Fact]
    public async Task ASecondProposalQueuesUntilTheLiveRevisionEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var launcher = new FakeLauncher();
        await using var brain = await StartAsync(launcher, ct);
        var index = brain.Get<IRevisions>("owner");
        var first = await index.Propose(new("os-runtime", "first", new(), Plan));
        var second = await index.Propose(new("os-runtime", "second", new(), Plan));
        var queued = brain.Get<IRevision>(IRevision.Key("owner", second));
        Assert.Equal(RevisionStage.Queued, (await queued.Read()).Stage);
        Assert.Equal(first, await index.Live());
        Assert.DoesNotContain("start sha0002", launcher.Calls);

        await using var changes = await brain.Observe<RevisionChanged>(queued, ct);
        var live = brain.Get<IRevision>(IRevision.Key("owner", first));
        await using var liveChanges = await brain.Observe<RevisionChanged>(live, ct);
        while ((await live.Read()).Stage != RevisionStage.Healthy) { await liveChanges.NextAsync(ct: ct); }
        await live.Discard();
        while ((await queued.Read()).Stage != RevisionStage.Healthy) { await changes.NextAsync(ct: ct); }
        Assert.Equal(second, await index.Live());
        Assert.Contains("start sha0002", launcher.Calls);
    }
}