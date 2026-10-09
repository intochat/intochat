using System.Diagnostics;
using DigitalBrain.Revisions;
using DigitalBrain.Revisions.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Revisions.Tests;

public sealed class RevisionFacts
{
    private static readonly VerificationPlan Plan = new([new("DigitalBrain.Apps.IApp", "owner/apps/calculator", "Read", "[]", "*")], ["OperatingSystem"], [], TimeSpan.FromMinutes(5));

    private static Task<ModuleBrain> StartAsync(FakeLauncher launcher, FakeVerifier verifier, CancellationToken ct)
        => ModuleTest.Create().WithModule<RevisionsModule>()
            .ConfigureSilo(silo => { silo.Services.AddSingleton<ILauncherClient>(launcher); silo.Services.AddSingleton<IRevisionVerifier>(verifier); })
            .StartAsync(ct);

    [Fact]
    public async Task ARevisionWalksProposedStartedHealthyVerifiedPromoted()
    {
        var ct = TestContext.Current.CancellationToken;
        var launcher = new FakeLauncher();
        await using var brain = await StartAsync(launcher, new FakeVerifier(), ct);
        var revision = brain.Get<IRevision>(IRevision.Key("owner", "sha0001"));
        await using var started = await brain.Observe<RevisionStarted>(revision, ct);
        await using var healthy = await brain.Observe<RevisionHealthy>(revision, ct);
        await using var verified = await brain.Observe<RevisionVerified>(revision, ct);
        await using var promoted = await brain.Observe<RevisionPromoted>(revision, ct);

        Assert.Equal(RevisionStage.Proposed, (await revision.Propose("os-runtime", @"E:\wt\sha0001", "change", Plan)).Stage);
        Assert.Equal(RevisionStage.Healthy, (await revision.Start()).Stage);
        Assert.Equal("http://localhost:5091", (await started.NextAsync(ct: ct)).McpEndpoint);
        Assert.Equal("sha0001", (await healthy.NextAsync(ct: ct)).Sha);
        Assert.Equal(RevisionStage.Verified, (await revision.Verify(Plan)).Stage);
        Assert.True((await verified.NextAsync(ct: ct)).Report.Passed);
        var final = await revision.Promote();
        Assert.Equal(RevisionStage.Promoted, final.Stage);
        Assert.Equal("sha0001", (await promoted.NextAsync(ct: ct)).Sha);
        Assert.Equal([RevisionStage.Proposed, RevisionStage.Starting, RevisionStage.Started, RevisionStage.Healthy, RevisionStage.Verifying, RevisionStage.Verified, RevisionStage.Promoted], final.History);
        Assert.Equal(["start sha0001", "healthy sha0001", "promote sha0001"], launcher.Calls);
    }

    [Fact]
    public async Task PromoteIsRefusedBeforeVerification()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(new FakeLauncher(), new FakeVerifier(), ct);
        var revision = brain.Get<IRevision>(IRevision.Key("owner", "sha0001"));
        await using var promoted = await brain.Observe<RevisionPromoted>(revision, ct);
        await revision.Propose("os-runtime", @"E:\wt\sha0001", "change", Plan);
        await revision.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(revision.Promote);
        Assert.Contains("Verified", error.Message, StringComparison.Ordinal);
        Assert.Equal(RevisionStage.Healthy, (await revision.Read()).Stage);
        Assert.Empty(promoted.Snapshot);
    }

    [Fact]
    public async Task AMismatchedExpectationNamesTheCheckThatFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = new FakeVerifier
        {
            Answer = (_, _) => Task.FromResult(new VerificationReport(
                [new("context", true, ""), new("call IApp owner/apps/calculator Read", false, "expected percent in operations")], TimeSpan.FromSeconds(1))),
        };
        await using var brain = await StartAsync(new FakeLauncher(), verifier, ct);
        var revision = brain.Get<IRevision>(IRevision.Key("owner", "sha0001"));
        await using var failed = await brain.Observe<RevisionFailed>(revision, ct);
        await revision.Propose("os-runtime", @"E:\wt\sha0001", "change", Plan);
        await revision.Start();
        var snapshot = await revision.Verify(Plan);
        Assert.Equal(RevisionStage.Failed, snapshot.Stage);
        var signal = await failed.NextAsync(ct: ct);
        Assert.Equal(RevisionStage.Verifying, signal.Stage);
        Assert.Equal("call IApp owner/apps/calculator Read: expected percent in operations", signal.Reason);
        Assert.Equal("call IApp owner/apps/calculator Read", snapshot.Report!.FirstFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(revision.Promote);
    }

    [Fact]
    public async Task ExceedingTheBudgetFailsTheVerifyStageOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;
        var verifier = new FakeVerifier
        {
            Answer = async (_, token) => { attempts++; await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new UnreachableException(); },
        };
        await using var brain = await StartAsync(new FakeLauncher(), verifier, ct);
        var revision = brain.Get<IRevision>(IRevision.Key("owner", "sha0001"));
        await using var failed = await brain.Observe<RevisionFailed>(revision, ct);
        await revision.Propose("os-runtime", @"E:\wt\sha0001", "change", Plan);
        await revision.Start();
        var snapshot = await revision.Verify(Plan with { Budget = TimeSpan.FromMilliseconds(100) });
        Assert.Equal(RevisionStage.Failed, snapshot.Stage);
        Assert.Equal("budget 00:00:00.1000000 exceeded", (await failed.NextAsync(ct: ct)).Reason);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AChildThatNeverTurnsHealthyFailsTheStartStage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(new FakeLauncher { Healthy = false }, new FakeVerifier(), ct);
        var revision = brain.Get<IRevision>(IRevision.Key("owner", "sha0001"));
        await using var failed = await brain.Observe<RevisionFailed>(revision, ct);
        await revision.Propose("os-runtime", @"E:\wt\sha0001", "change", Plan);
        Assert.Equal(RevisionStage.Failed, (await revision.Start()).Stage);
        Assert.Equal(RevisionStage.Started, (await failed.NextAsync(ct: ct)).Stage);
    }

    [Fact]
    public async Task DiscardStopsTheChildAndKeepsTheWorktreeOnTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var launcher = new FakeLauncher();
        await using var brain = await StartAsync(launcher, new FakeVerifier(), ct);
        var revision = brain.Get<IRevision>(IRevision.Key("owner", "sha0001"));
        await revision.Propose("os-runtime", @"E:\wt\sha0001", "change", Plan);
        await revision.Start();
        var discarded = await revision.Discard();
        Assert.Equal(RevisionStage.Discarded, discarded.Stage);
        Assert.Equal(@"E:\wt\sha0001", discarded.Worktree);
        Assert.Contains("discard sha0001", launcher.Calls);
    }
}