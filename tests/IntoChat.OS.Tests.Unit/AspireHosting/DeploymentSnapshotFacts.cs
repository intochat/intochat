namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DeploymentSnapshotFacts
{
    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public void OnlyTheAiSectionsChanged(string topology)
    {
        var recorded = DeploymentSnapshot.Load($"deployment-{topology}.json")
            .Without("ai", "devices", "webtools", "mcp");
        var actual = DeploymentSnapshot.Of(AppHostChainFacts.Compose(topology))
            .Without("ai", "devices", "webtools", "mcp");

        Assert.Equal(recorded, actual);
    }
}
