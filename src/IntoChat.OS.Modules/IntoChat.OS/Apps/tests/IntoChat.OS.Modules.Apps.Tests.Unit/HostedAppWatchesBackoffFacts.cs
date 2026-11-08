namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class HostedAppWatchesBackoffFacts
{
    [Fact]
    public void AFailedWatchRestartsAfterADoublingDelayCappedAtAMinute()
    {
        Assert.Equal([1d, 2d, 4d, 8d, 16d, 32d, 60d, 60d], Enumerable.Range(1, 8).Select(attempt => HostedAppWatches.Backoff(attempt).TotalSeconds));
    }
}
