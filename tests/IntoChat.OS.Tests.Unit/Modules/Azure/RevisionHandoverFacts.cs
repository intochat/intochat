namespace IntoChat.OS.Modules.Microsoft.Azure.Tests.Unit;

// Stop-then-start of a silo app (ArmAzure, ContainerAppSpec.StopBeforeUpdate): whatever happens to the update, the app ends
// with a running revision, the new one or the one stopped for it.
public sealed class RevisionHandoverFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUpdate_StopsAndDrainsTheOldRevisionFirst_AndKeepsTheNewOne()
    {
        var app = new FakeApp("r1");
        var made = await RevisionHandover.StopThenUpdate(app, () => app.Update(makesRevision: "r2", ready: true), 3, 3, app.Pause, Ct);

        Assert.Equal("made", made);
        Assert.Equal(["deactivate r1", "update"], app.Log);
        Assert.False(app.Revisions["r1"].Active);
        Assert.True(app.Revisions["r2"].Active);
    }

    [Fact]
    public async Task AnUpdateThatFails_BringsTheStoppedRevisionBack()
    {
        var app = new FakeApp("r1");
        var failure = new InvalidOperationException("template refused");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RevisionHandover.StopThenUpdate<string>(app, () => throw failure, 3, 3, app.Pause, Ct));

        Assert.Same(failure, thrown);
        Assert.Equal(["deactivate r1", "activate r1"], app.Log);
        Assert.True(app.Revisions["r1"].Active);
    }

    [Fact]
    public async Task ANewRevisionThatNeverBecomesReady_IsDeactivated_AndTheStoppedOneRunsAgain()
    {
        var app = new FakeApp("r1");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RevisionHandover.StopThenUpdate(app, () => app.Update(makesRevision: "r2", ready: false), 3, 3, app.Pause, Ct));

        Assert.Equal(["deactivate r1", "update", "deactivate r2", "activate r1"], app.Log);
        Assert.True(app.Revisions["r1"].Active);
        Assert.False(app.Revisions["r2"].Active);
    }

    [Fact]
    public async Task AnUpdateThatMadeNoRevision_ActivatesTheLatestAgain()
    {
        var app = new FakeApp("r1");

        await RevisionHandover.StopThenUpdate(app, () => app.Update(makesRevision: null, ready: true), 3, 3, app.Pause, Ct);

        Assert.Equal(["deactivate r1", "update", "activate r1"], app.Log);
        Assert.True(app.Revisions["r1"].Active);
    }

    [Fact]
    public async Task ADrainThatTakesTooLong_DelaysTheUpdate_WithoutLosingIt()
    {
        var app = new FakeApp("r1") { Drains = false };

        await RevisionHandover.StopThenUpdate(app, () => app.Update(makesRevision: "r2", ready: true), 3, 3, app.Pause, Ct);

        Assert.Equal(3, app.Pauses);
        Assert.Equal(["deactivate r1", "update"], app.Log);
        Assert.True(app.Revisions["r2"].Active);
    }

    [Fact]
    public async Task AnAppLeftStoppedByAnEarlierRun_IsActivatedAgain_AndARunningAppIsLeftAlone()
    {
        var stopped = new FakeApp("r1");
        stopped.Revisions["r1"] = stopped.Revisions["r1"] with { Active = false, Replicas = 0 };
        await RevisionHandover.EnsureActive(stopped, Ct);
        Assert.Equal(["activate r1"], stopped.Log);

        var running = new FakeApp("r1");
        await RevisionHandover.EnsureActive(running, Ct);
        Assert.Empty(running.Log);
    }

    private sealed class FakeApp : IAppRevisions
    {
        public FakeApp(string running)
        {
            Revisions[running] = new AppRevision(running, Active: true, Replicas: 1);
            (Latest, LatestReady) = (running, running);
        }

        public Dictionary<string, AppRevision> Revisions { get; } = new(StringComparer.Ordinal);
        public List<string> Log { get; } = [];
        public bool Drains { get; init; } = true;
        public int Pauses { get; private set; }
        private string Latest { get; set; }
        private string LatestReady { get; set; }

        public Task<string> Update(string? makesRevision, bool ready)
        {
            Log.Add("update");
            if (makesRevision is not null)
            {
                Revisions[makesRevision] = new AppRevision(makesRevision, Active: true, Replicas: 1);
                Latest = makesRevision;
                if (ready) { LatestReady = makesRevision; }
            }
            return Task.FromResult("made");
        }

        public Task Pause(CancellationToken _)
        {
            Pauses++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AppRevision>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<AppRevision>>([.. Revisions.Values]);

        public Task<(string? Latest, string? LatestReady)> Heads(CancellationToken ct) => Task.FromResult<(string?, string?)>((Latest, LatestReady));

        public Task Deactivate(string name, CancellationToken ct)
        {
            Log.Add("deactivate " + name);
            Revisions[name] = Revisions[name] with { Active = false, Replicas = Drains ? 0 : Revisions[name].Replicas };
            return Task.CompletedTask;
        }

        public Task Activate(string name, CancellationToken ct)
        {
            Log.Add("activate " + name);
            Revisions[name] = Revisions[name] with { Active = true, Replicas = 1 };
            return Task.CompletedTask;
        }
    }
}
