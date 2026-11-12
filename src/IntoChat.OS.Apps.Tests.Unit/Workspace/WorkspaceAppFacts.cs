using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Workspace;

public sealed class WorkspaceAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> WorkspaceOperations
        => ScreenBaseline.Cases(typeof(WorkspaceApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(WorkspaceOperations))]
    public Task WorkspaceBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task WorkspaceScenariosPass()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(WorkspaceApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.workspace", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public void WorkspaceCarriesTheFlutterTree()
    {
        Assert.Equal("workspace", WorkspaceApp.Bundle.Name);
    }
}
