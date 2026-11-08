using DigitalBrain.Testing;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Workspace;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

public sealed class DesktopAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> DesktopOperations => AppBaseline.Cases(typeof(DesktopApp));

    [Theory, MemberData(nameof(DesktopOperations))]
    public Task DesktopBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task DesktopScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<DesktopApp>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }

    [Fact]
    public void WorkspaceAndDesktopCarryTheFlutterTrees()
    {
        Assert.Equal("workspace", WorkspaceApp.Bundle.Name);
        Assert.Equal("desktop", DesktopApp.Bundle.Name);
    }

    [Fact]
    public async Task OpenWithAppAndViewRoutesToThatView()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        os.Register<RecordingApp>();
        var desktop = await os.Install<DesktopApp>();

        await desktop.Request("open", new DesktopOpen("trace", "trace/t-1"));

        Assert.Equal(("apps/trace", "open", "trace/t-1"), Assert.Single(RecordingApp.Requests));
    }

    [Fact]
    public async Task LegacyOpenByNameStillOpensTheWindow()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        var desktop = await os.Install<DesktopApp>();

        // "open:tasks" binds the name alone; no app neuron is registered, so a forward would throw.
        await desktop.Request("open", new DesktopOpen("tasks"));
        await desktop.Request("open", new DesktopOpen("trace", "trace/t-1"));

        Assert.Empty(RecordingApp.Requests);
        Assert.Null(desktop.App.RecordedError);
    }
}
