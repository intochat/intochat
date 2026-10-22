namespace IntoChat.OS.Architecture.Tests.Unit;

// The AppHost projects a run-mode marker to the processes it starts, and the Open-posture loopback guard
// reads it. The key is a deliberate literal here: renaming it silently disables the guard.
public sealed class RunModeMarkerFacts
{
    [Fact]
    public void TheRunModeMarkerKeepsItsLiteral()
    {
        Assert.Equal("IntoChatOS:Hosting:Mode", IntoChat.OS.Framework.IntoChatOSNames.HostingModeConfigurationKey);
        Assert.Equal("run", IntoChat.OS.Framework.IntoChatOSNames.HostingModeRun);
    }

    [Fact]
    public void TheLoopbackGuardReadsTheMarkerThroughTheSharedConstant()
    {
        var files = Repository.ProjectFiles("OpenPostureLoopback.cs").ToArray();
        var file = Assert.Single(files);
        Assert.Contains("IntoChatOSNames.HostingModeConfigurationKey", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
