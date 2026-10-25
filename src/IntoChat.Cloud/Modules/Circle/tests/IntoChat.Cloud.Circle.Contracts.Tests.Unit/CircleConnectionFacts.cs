namespace IntoChat.Cloud.Circle.Tests.Unit;

public sealed class CircleConnectionFacts
{
    [Fact]
    public void ConnectionStatesCoverTheJourneys()
        => Assert.Equal(
            ["NotJoined", "Joining", "PendingAccount", "Connected", "Connecting", "Offline", "Unavailable", "Disconnected", "Problem"],
            Enum.GetNames<CircleConnectionState>());

    [Theory]
    [InlineData(JoinStep.Preparing, 0.1)]
    [InlineData(JoinStep.Redeeming, 0.5)]
    [InlineData(JoinStep.Connecting, 0.9)]
    [InlineData(JoinStep.Done, 1.0)]
    public void JoinStepsHaveFixedProgress(JoinStep step, double progress) => Assert.Equal(progress, JoinSteps.Progress(step));

    [Fact]
    public void JoinFailuresMapOneToOneToTheCopyRows()
        => Assert.Equal(["Rejected", "Expired", "RateLimited", "Unreachable", "KeyCustody", "ServerUnverified", "Timeout"], Enum.GetNames<JoinFailure>());

    [Fact]
    public void SyncStatesCoverWhatIsSynced()
        => Assert.Equal(["Off", "UpToDate", "Behind", "Pushing", "Waiting", "Error", "Resync", "Paused"], Enum.GetNames<SyncState>());

    [Fact]
    public void StatusCarriesNoSecrets()
    {
        string[] banned = ["token", "privatekey", "secret", "password", "spki"];
        var members = new[] { typeof(CircleConnectionStatus), typeof(BrainSyncStatus), typeof(JoinRequest), typeof(JoinSucceeded) }
            .SelectMany(type => type.GetProperties()).Select(property => property.DeclaringType!.Name + "." + property.Name);
        Assert.DoesNotContain(members, member => banned.Any(word => member.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task TheReleaseComputeDelegateAnswersUnavailable()
    {
        IComputeDelegate compute = new UnavailableComputeDelegate();
        Assert.Equal(ComputeDelegateStatus.NotDelegated, compute.Status);
        var result = await compute.DelegateCompute(new ComputeRequest("inference", new byte[] { 1 }));
        Assert.Equal(ComputeOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Payload);
    }
}
