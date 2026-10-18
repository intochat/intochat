namespace IntoChat.OS.Microsoft.Aspire.Tests;

public sealed class CandidateQueueFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ASecondProposalQueuesUntilTheFirstEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { Healthy = false };
        await using var brain = await CandidateFacts.StartAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);
        var index = brain.Get<ICandidates>(ICandidates.Key(brain.Owner.BrainId));
        var second = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0002"));
        await using var secondStarted = await brain.Observe<CandidateStarted>(second, ct);

        var first = await index.Propose(new("b", "one", [], CandidateFacts.Plan));
        var queued = await index.Propose(new("b", "two", [], CandidateFacts.Plan));

        Assert.Equal("sha0001", first);
        Assert.Equal("sha0002", queued);
        Assert.Equal("sha0002", (await secondStarted.NextAsync(ct: ct)).Sha);
        Assert.True(host.Calls.IndexOf("remove sha0001") < host.Calls.IndexOf("run sha0002"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheCandidateIsPreparedFromTheLiveClusterSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost { Current = "live123" };
        await using var brain = await CandidateFacts.StartAsync(host, cluster, new FakeVerifier(), ct);

        await brain.Get<ICandidates>(ICandidates.Key(brain.Owner.BrainId)).Propose(new("b", "one", [], CandidateFacts.Plan));

        Assert.Equal("live123", host.PreparedFrom);
    }
}
