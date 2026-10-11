using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.WebBrowser;
using DigitalBrain.Testing;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.WebBrowser;

[Collection(FlutterHostCollection.Name)]
public sealed class WebBrowserHttpFacts(FlutterHostFixture host)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task NavigateUpdatesSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        using var navigate = await person.Http.PostAsJsonAsync($"/brains/{ws}/ui/browsers/docs/navigate",
            new { uri = "https://learn.microsoft.com/", title = "Docs" }, ct);
        Assert.Equal(HttpStatusCode.Accepted, navigate.StatusCode);
        var state = await person.Http.GetFromJsonAsync<WebBrowserView>($"/brains/{ws}/ui/browsers/docs", Json, ct);
        Assert.Equal("https://learn.microsoft.com/", state!.Uri);
        Assert.Equal("Docs", state.Title);
        Assert.Equal(BrowserConnection.Waiting, state.Connection);
        Assert.Empty(state.Error);
        Assert.Empty(state.ActualUrl);
    }
}
