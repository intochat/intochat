using Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DeploymentSnapshotFacts
{
    private static readonly string KernelPath = DeploymentSnapshot.ProjectPath("IntoChat.OS.Kernel");

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public void CompositionMatchesRecordedSnapshot(string topology)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [$"--IntoChatOS:Topology={topology}", "--IntoChatOS:Shell:Enabled=false"],
            DisableDashboard = true,
        });
        var profile = DeploymentProfile.From(builder);
        var os = builder.AddIntoChatOS("Modules", profile);
        os.AddKernel(KernelPath, profile);

        var actual = DeploymentSnapshot.Of(os);
        var fileName = $"deployment-{topology}.json";
        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS"), "1", StringComparison.Ordinal))
        {
            DeploymentSnapshot.SaveSource(fileName, actual);
        }

        Assert.Equal(DeploymentSnapshot.Load(fileName), actual);
    }
}
