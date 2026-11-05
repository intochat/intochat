namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Unit;

public sealed class OrleansContractsFacts
{
    [Fact]
    public void ClusterChangedCanBeConstructed()
    {
        var snapshot = new ClusterSnapshot([], 1);
        var signal = new ClusterChanged(snapshot);
        Assert.NotNull(signal);
        Assert.Equal(1, signal.Snapshot.Version);
    }
}
