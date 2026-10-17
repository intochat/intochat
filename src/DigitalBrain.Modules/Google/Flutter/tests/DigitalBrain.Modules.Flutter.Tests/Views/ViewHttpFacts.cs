using System.Net;
using DigitalBrain.Flutter.Views;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Views;

[Collection(FlutterHostCollection.Name)]
public sealed class ViewHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task MaximumDepthIsReadableOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct);
        using var caller = person.AsCaller();
        var name = person.BrainId + "/apps/test/deep-view";
        var root = Ui.Text("leaf", "deep leaf");
        for (var i = 0; i < 31; i++) { root = Ui.Column("level" + i, [root]); }
        await host.Brain.Get<IView>(name).Publish(new(1, 1, root));
        var snapshot = await person.Http.GetStringAsync($"/brains/{person.BrainId}/apps/node?kind=view&name={Uri.EscapeDataString(name)}", ct);
        Assert.Contains("deep leaf", snapshot);
    }
    [Fact]
    public async Task AnotherAppCannotPublishAnOwnersView()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct);
        var app = person.BrainId + "/apps/owner";
        var view = host.Brain.Get<IView>(app + "/view");
        using (person.AsApp(app)) { await view.Publish(new(1, 1, Ui.Text("title", "owner"))); }
        using (person.AsApp(person.BrainId + "/apps/intruder"))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.Publish(new(1, 2, Ui.Text("title", "forged")))); }
        using (person.AsCaller()) { Assert.Equal("owner", (await view.Read())!.Root.Text); }
    }

    [Fact]
    public async Task HttpUsesCanonicalActionAndRequiresRequestIdentityAndBrainOwnership()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct);
        using var caller = person.AsCaller();
        var name = person.BrainId + "/apps/test/view";
        var view = host.Brain.Get<IView>(name);
        await view.Publish(new(4, 1, Ui.Button("go", "Go", "canonical")));
        await using var actions = await host.Brain.Observe<ViewAction>(view, ct);
        var request = Guid.NewGuid();
        using var ok = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "view", name, revision = 4, field = "go", action = "canonical", operationId = request, value = "forged payload" }, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var action = await actions.NextAsync(ct: ct);
        Assert.Equal(("canonical", request), (action.Action, action.RequestId));
        using var missing = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "view", name, revision = 4, field = "go", action = "canonical" }, ct);
        Assert.False(missing.IsSuccessStatusCode);
        using var stale = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "view", name, revision = 3, field = "go", action = "canonical", operationId = Guid.NewGuid() }, ct);
        Assert.False(stale.IsSuccessStatusCode);
        var snapshot = await person.Http.GetStringAsync($"/brains/{person.BrainId}/apps/node?kind=view&name={Uri.EscapeDataString(name)}", ct);
        Assert.Contains("canonical", snapshot);
        using var other = await person.Http.GetAsync($"/brains/{person.BrainId}/apps/node?kind=view&name=other%2Fapps%2Ftest%2Fview", ct);
        Assert.False(other.IsSuccessStatusCode);
    }
}
