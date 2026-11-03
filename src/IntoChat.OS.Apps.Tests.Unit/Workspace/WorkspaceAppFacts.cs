using DigitalBrain.Testing;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Workspace;

public sealed class WorkspaceAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> WorkspaceOperations
        => AppBaseline.Cases(typeof(WorkspaceApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(WorkspaceOperations))]
    public Task WorkspaceBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task WorkspaceScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<WorkspaceApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }

    [Fact]
    public void WorkspaceCarriesTheFlutterTree()
    {
        Assert.Equal("workspace", WorkspaceApp.Bundle.Name);
    }
}
