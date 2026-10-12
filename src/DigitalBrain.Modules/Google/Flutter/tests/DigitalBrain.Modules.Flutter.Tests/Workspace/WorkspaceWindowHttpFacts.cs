using System.Net;
using DigitalBrain.Flutter.Workspace;

namespace DigitalBrain.Modules.Flutter.Tests.Workspace;

[Collection(FlutterHostCollection.Name)]
public sealed class WorkspaceWindowHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task CloseAndReopenPreserveOpaqueWindowIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var workspace = brain.Get<IWorkspace>(ws);
        var ids = new[] { "apps/chart/window", "apps%2Fchart%2Fwindow" };
        foreach (var id in ids)
        {
            var before = await workspace.Read();
            await workspace.Open(new(Guid.NewGuid().ToString(), id, id,
                new("surface", ws + "/apps/chart/surface"), before.Revision));
        }

        foreach (var id in ids)
        {
            var before = await workspace.Read();
            var path = $"/brains/{ws}/windows/{Uri.EscapeDataString(id)}";
            using var close = await person.Http.PostAsJsonAsync(path + "/close",
                new { windowId = id, operationId = Guid.NewGuid().ToString(), expectedRevision = before.Revision }, ct);
            Assert.Equal(HttpStatusCode.OK, close.StatusCode);
            var closed = await workspace.Read();
            Assert.False(closed.Windows.Single(w => w.Id == id).IsOpen);
            Assert.True(closed.Windows.Single(w => w.Id != id).IsOpen);

            using var reopen = await person.Http.PostAsJsonAsync(path + "/reopen",
                new { windowId = id, operationId = Guid.NewGuid().ToString(), expectedRevision = closed.Revision }, ct);
            Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
            Assert.All((await workspace.Read()).Windows, w => Assert.True(w.IsOpen));
        }
    }
}
