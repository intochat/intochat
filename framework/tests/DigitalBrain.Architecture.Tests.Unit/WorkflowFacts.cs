namespace DigitalBrain.Architecture.Tests.Unit;

public sealed class WorkflowFacts
{
    [Fact]
    public void FrameworkWorkflowsAreUnifiedAtRepositoryRoot()
    {
        Assert.False(Directory.Exists(Path.Combine(Repository.Root, ".github", "workflows")),
            "framework/.github/workflows must not exist; workflows live at the repository root.");

        var rootWorkflows = Path.GetFullPath(Path.Combine(Repository.Root, "..", ".github", "workflows"));
        Assert.True(File.Exists(Path.Combine(rootWorkflows, "ci.yml")), "Root .github/workflows/ci.yml must exist.");
        Assert.True(File.Exists(Path.Combine(rootWorkflows, "release.yml")), "Root .github/workflows/release.yml must exist.");
    }
}
