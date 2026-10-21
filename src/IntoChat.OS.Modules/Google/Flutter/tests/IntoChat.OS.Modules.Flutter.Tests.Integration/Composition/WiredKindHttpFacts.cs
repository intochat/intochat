using System.Net;
using System.Text.Json;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Expander;
using IntoChat.OS.Flutter.Image;
using IntoChat.OS.Flutter.InfoBar;
using IntoChat.OS.Flutter.Progress;
using IntoChat.OS.Flutter.Toggle;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Composition;

public sealed class WiredKindHttpFacts(FlutterHost host)
{
    [Fact]
    public async Task ToggleFlipInfoBarDismissAndExpanderToggleArriveThroughTheAppEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var toggleName = person.BrainId + "/apps/kit/dark";
        var barName = person.BrainId + "/apps/kit/notice";
        var toggle = host.Brain.Get<IToggle>(toggleName);
        var bar = host.Brain.Get<IInfoBar>(barName);
        var expander = host.Brain.Get<IExpander>(person.BrainId + "/apps/kit/more");
        await expander.Set("More", false);
        await toggle.Set("Dark", true);
        await bar.Show("error", "Failed", "Retry later");
        using var flip = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "toggle", name = toggleName, action = "flip" }, ct);
        using var dismiss = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "infobar", name = barName, action = "dismiss" }, ct);
        using var open = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "expander", name = person.BrainId + "/apps/kit/more", action = "toggle" }, ct);
        using var unknown = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "toggle", name = toggleName, action = "explode" }, ct);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.False(unknown.IsSuccessStatusCode);
        Assert.True((await expander.Read()).Expanded);
        Assert.Equal(HttpStatusCode.OK, flip.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dismiss.StatusCode);
        Assert.False((await toggle.Read()).On);
        Assert.False((await bar.Read()).Visible);
    }

    [Fact]
    public async Task NewlyWiredKindsAreReadableNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var prefix = person.BrainId + "/apps/kit/";
        await host.Brain.Get<IToggle>(prefix + "t").Set("Dark", true);
        await host.Brain.Get<IProgress>(prefix + "p").Set(true, .4, "Upload");
        await host.Brain.Get<IInfoBar>(prefix + "i").Show("warning", "Heads up", "Body");
        await host.Brain.Get<IImage>(prefix + "m").Set("https://example.test/a.png", "image/png", "A cat");
        await host.Brain.Get<IExpander>(prefix + "e").Set("More", true, [new UiChildRef("toggle", prefix + "t")]);
        foreach (var (kind, name, field, expected) in new[]
        {
            ("toggle", "t", "label", "Dark"), ("progress", "p", "label", "Upload"),
            ("infobar", "i", "severity", "warning"), ("image", "m", "prompt", "A cat"), ("expander", "e", "header", "More"),
        })
        {
            using var doc = JsonDocument.Parse(await person.Http.GetStringAsync(
                $"/brains/{person.BrainId}/apps/node?kind={kind}&name={Uri.EscapeDataString(prefix + name)}", ct));
            Assert.Equal(expected, doc.RootElement.GetProperty(field).GetString());
        }
    }
}
