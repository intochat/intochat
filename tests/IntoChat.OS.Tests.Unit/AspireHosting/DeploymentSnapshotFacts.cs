namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DeploymentSnapshotFacts
{
    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public void OnlyTheAiSectionsChanged(string topology)
    {
        var actual = DeploymentSnapshot.Of(AppHostChainFacts.Compose(topology))
            .Without("ai", "devices", "webtools", "mcp");
        var recorded = DeploymentSnapshot.Load($"deployment-{topology}.json")
            .Without("ai", "devices", "webtools", "mcp");

        Assert.True(recorded.Equals(actual), Difference(recorded, actual));
    }

    private static string Difference(DeploymentSnapshot recorded, DeploymentSnapshot actual)
    {
        var lines = new List<string>();
        lines.AddRange(recorded.Modules.Except(actual.Modules).Select(m => $"- module {m}"));
        lines.AddRange(actual.Modules.Except(recorded.Modules).Select(m => $"+ module {m}"));
        lines.AddRange(recorded.Resources.Except(actual.Resources).Select(r => $"- resource {r}"));
        lines.AddRange(actual.Resources.Except(recorded.Resources).Select(r => $"+ resource {r}"));
        foreach (var key in recorded.Settings.Keys.Union(actual.Settings.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            recorded.Settings.TryGetValue(key, out var was);
            actual.Settings.TryGetValue(key, out var now);
            if (!string.Equals(was, now, StringComparison.Ordinal)) { lines.Add($"~ {key}: '{was}' -> '{now}'"); }
        }

        return "Deployment snapshot differs:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
