using DigitalBrain.Testing;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

[Collection(nameof(RecordingApp))]
public sealed class DesktopAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> DesktopOperations => AppBaseline.Cases(typeof(DesktopApp), os => os.Register<DesktopRecordsNeuron>());

    [Theory, MemberData(nameof(DesktopOperations))]
    public async Task DesktopBaseline(AppOperationCase operation)
    {
        using var person = Edge.AsPerson();
        await AppBaseline.RunAsync(operation);
    }

    [Fact]
    public async Task DesktopScenariosPass()
    {
        using var person = Edge.AsPerson();
        var report = await BddRunner.VerifyAsync<DesktopApp>(fakes: os => os.Register<DesktopRecordsNeuron>(), cancellationToken: TestContext.Current.CancellationToken);
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

        await desktop.Request("open", new DesktopOpen("creator", "trace/t-1"));

        Assert.Equal(("apps/creator", "open", "trace/t-1"), Assert.Single(RecordingApp.Requests));
    }

    [Fact]
    public async Task LegacyOpenByNameStillOpensTheWindow()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        var desktop = await os.Install<DesktopApp>();

        // "open:tasks" binds the name alone; no app neuron is registered, so a forward would throw.
        await desktop.Request("open", new DesktopOpen("tasks"));
        await desktop.Request("open", new DesktopOpen("creator", "trace/t-1"));

        Assert.Empty(RecordingApp.Requests);
        Assert.Null(desktop.App.RecordedError);
    }
}
