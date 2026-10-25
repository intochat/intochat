using IntoChat.OS.Modules.Flutter.Tabs;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Tabs;

public sealed class TabsFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetThenSelect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var tabs = brain.Get<ITabs>("pages");
        await tabs.Set(
            [new TabItem("a", "A", new UiChildRef("text", "about")), new TabItem("b", "B", new UiChildRef("chart", "btc"))],
            "a");
        await tabs.Select("b");
        Assert.Equal("b", (await tabs.Read()).SelectedId);
    }
}
