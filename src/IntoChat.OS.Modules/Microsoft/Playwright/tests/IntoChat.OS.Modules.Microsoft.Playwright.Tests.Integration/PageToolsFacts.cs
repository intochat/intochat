using IntoChat.OS.Testing.Fakes;
using IntoChat.OS.Modules.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Playwright.Tests.Integration;

public sealed class PageToolsFacts(PlaywrightHost host)
{
    [Fact]
    [Trait("host", "own")]
    public async Task ConfiguringOffersTheThreePageTools()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new FakeBrowserSessionProvider { Url = FilmCraftUrl, Title = "FilmCraft" });
        using var caller = brain.Owner.AsCaller();

        var pages = brain.Get<IPageTools>(brain.Owner.Key("pages/filmcraft"));
        Assert.Empty((await pages.Tools(ct)).Tools);

        await pages.Configure(new("FilmCraft", brain.Owner.Key("driver"), "filmcraft"), ct);

        var offered = await pages.Tools(ct);
        Assert.Equal("FilmCraft", offered.Title);
        Assert.Equal(["commands", "execute", "request"], offered.Tools.Select(tool => tool.Name));
    }

    [Fact]
    [Trait("host", "own")]
    public async Task ExecuteEvaluatesTheApisExecuteWithEncodedArguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { Url = FilmCraftUrl, Title = "FilmCraft", Evaluate = _ => "{\"ok\":true}" };
        await using var brain = await Start(provider);
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IPlaywright>(brain.Owner.Key("driver")).Attach(new(1234, new string('a', 32)), ct);
        var tools = brain.Get<IPageTools>(brain.Owner.Key("pages/filmcraft"));
        await tools.Configure(new("FilmCraft", brain.Owner.Key("driver"), "filmcraft"), ct);

        var result = await tools.Run("execute", """{"command":"timeline.place\"); alert(1); (\"","params":{"track":"V1"}}""", ct);

        Assert.False(result.IsError);
        Assert.Equal("{\"ok\":true}", result.Text);
        var expression = Assert.Single(provider.Expressions);
        var command = System.Text.Json.JsonSerializer.Serialize("timeline.place\"); alert(1); (\"");
        Assert.Contains("window.filmcraft.execute(" + command + ", {\"track\":\"V1\"})", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("\"); alert(1)", expression, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task CommandsAreFilteredByIdSubstring()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { Url = FilmCraftUrl, Title = "FilmCraft", Evaluate = _ => """[{"id":"timeline.place"},{"id":"color.lumetri"},{"id":"timeline.razor"}]""" };
        await using var brain = await Start(provider);
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IPlaywright>(brain.Owner.Key("driver")).Attach(new(1234, new string('a', 32)), ct);
        var tools = brain.Get<IPageTools>(brain.Owner.Key("pages/filmcraft"));
        await tools.Configure(new("FilmCraft", brain.Owner.Key("driver"), "filmcraft"), ct);

        var result = await tools.Run("commands", """{"filter":"timeline"}""", ct);

        Assert.Equal("""[{"id":"timeline.place"},{"id":"timeline.razor"}]""", result.Text);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task AHiddenPageIsAToolErrorNotAHang()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new FakeBrowserSessionProvider { Url = FilmCraftUrl, Title = "FilmCraft", EvaluateFailure = "Error: page not rendering" });
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IPlaywright>(brain.Owner.Key("driver")).Attach(new(1234, new string('a', 32)), ct);
        var tools = brain.Get<IPageTools>(brain.Owner.Key("pages/filmcraft"));
        await tools.Configure(new("FilmCraft", brain.Owner.Key("driver"), "filmcraft"), ct);

        var result = await tools.Run("request", """{"method":"ui.inspect"}""", ct);

        Assert.True(result.IsError);
        Assert.StartsWith("page not rendering", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApiNameThatIsNotAnIdentifierIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await host.PersonAsync("pages", ct);
        using var caller = owner.AsCaller();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Brain.Get<IPageTools>(owner.Key("pages/x"))
            .Configure(new("X", owner.Key("driver"), "filmcraft;alert(1)"), ct));
    }

    private const string FilmCraftUrl = "http://127.0.0.1:18081/";

    // The browser session host stands in for a CDP-attached page whose timing and failures the facts script.
    private static Task<ITestBrain> Start(FakeBrowserSessionProvider provider) => TestBrain.Create().WithModule<PlaywrightModule>().WithModule<ToolsModule>()
        .ConfigureServices(services => services.AddSingleton<IBrowserSessionProvider>(provider))
        .StartAsync(TestContext.Current.CancellationToken);
}
