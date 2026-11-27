namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

public sealed class SolutionQueriesFacts
{
    private static readonly string SourceFolder = "s" + "rc";

    [Theory]
    [InlineData("Components/Aspire/IntoChat.Aspire.csproj", "IntoChat.Aspire", "Components/Aspire", true)]
    [InlineData("Components/Aspire/Contracts/IntoChat.Aspire.Contracts.csproj", "IntoChat.Aspire.Contracts", "Components/Aspire", true)]
    [InlineData("Components/Notes/IntoChat.Notes.csproj", "IntoChat.Notes", "Components/Notes", true)]
    [InlineData("Modules/AI/IntoChat.OS.Modules.AI.csproj", "IntoChat.OS.Modules.AI", "Modules/AI", true)]
    [InlineData("Modules/Identity/Contracts/IntoChat.OS.Modules.Identity.Contracts.csproj", "IntoChat.OS.Modules.Identity.Contracts", "Modules/Identity", true)]
    [InlineData("IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", true)]
    [InlineData("tests/IntoChat.OS.Tests.Unit/IntoChat.OS.Tests.Unit.csproj", "IntoChat.OS.Tests.Unit", "IntoChat.OS.Tests.Unit", false)]
    public void ClusterOfSplitsComponentsAndModulesPerUnit(string relativePath, string projectName, string expectedCluster, bool underSourceRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "intochat-cluster-root");
        var segments = relativePath.Split('/');
        var fullPath = underSourceRoot
            ? Path.Combine([root, SourceFolder, .. segments])
            : Path.Combine([root, .. segments]);

        var cluster = SolutionQueries.ClusterOf(root, fullPath, projectName);

        Assert.Equal(expectedCluster, cluster);
    }

    [Fact]
    public void ClusterOfFallsBackToProjectNameWhenFilePathIsNull()
        => Assert.Equal("VirtualProject", SolutionQueries.ClusterOf(null, null, "VirtualProject"));
}
