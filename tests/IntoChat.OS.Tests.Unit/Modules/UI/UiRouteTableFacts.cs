using IntoChat.OS.Tests.Unit;

namespace IntoChat.OS.Modules.UI;

// The HTTP routes the UI module maps, pinned: the Widgetbook static site, which the apps module mapped until it moved here.
public sealed class UiRouteTableFacts
{
    private static readonly string[] Expected =
    [
        "GET /widgetbook anonymous order=-100",
        "GET /widgetbook/index.html anonymous order=-100",
        "GET /widgetbook/{**assetPath} anonymous order=-100",
    ];

    [Fact]
    public void TheUiModuleMapsExactlyTheseRoutes()
    {
        var routes = RouteTable.Of(new UiModule().Configure);

        Assert.True(Expected.SequenceEqual(routes), "The UI module maps:" + Environment.NewLine + string.Join(Environment.NewLine, routes));
    }
}
