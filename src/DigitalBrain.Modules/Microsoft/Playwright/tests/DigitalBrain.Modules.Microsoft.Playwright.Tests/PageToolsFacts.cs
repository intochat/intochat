using DigitalBrain.Microsoft.Playwright;
using DigitalBrain.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.Playwright.Tests;

public sealed class PageToolsFacts
{
    [Fact]
    public async Task ConfiguringOffersTheThreePageTools()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new Provider());
        using var caller = brain.Owner.AsCaller();

        var pages = brain.Get<IPageTools>(brain.Owner.Key("pages/filmcraft"));
        Assert.Empty((await pages.Tools(ct)).Tools);

        await pages.Configure(new("FilmCraft", brain.Owner.Key("driver"), "filmcraft"), ct);

        var offered = await pages.Tools(ct);
        Assert.Equal("FilmCraft", offered.Title);
        Assert.Equal(["commands", "execute", "request"], offered.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task ExecuteEvaluatesTheApisExecuteWithEncodedArguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new Provider { Result = "{\"ok\":true}" };
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
    public async Task CommandsAreFilteredByIdSubstring()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new Provider { Result = """[{"id":"timeline.place"},{"id":"color.lumetri"},{"id":"timeline.razor"}]""" };
        await using var brain = await Start(provider);
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IPlaywright>(brain.Owner.Key("driver")).Attach(new(1234, new string('a', 32)), ct);
        var tools = brain.Get<IPageTools>(brain.Owner.Key("pages/filmcraft"));
        await tools.Configure(new("FilmCraft", brain.Owner.Key("driver"), "filmcraft"), ct);

        var result = await tools.Run("commands", """{"filter":"timeline"}""", ct);

        Assert.Equal("""[{"id":"timeline.place"},{"id":"timeline.razor"}]""", result.Text);
    }

    [Fact]
    public async Task AHiddenPageIsAToolErrorNotAHang()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new Provider { Failure = "Error: page not rendering" });
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
        await using var brain = await Start(new Provider());
        using var caller = brain.Owner.AsCaller();

        await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IPageTools>(brain.Owner.Key("pages/x"))
            .Configure(new("X", brain.Owner.Key("driver"), "filmcraft;alert(1)"), ct));
    }

    // The browser session host stands in for a CDP-attached page whose timing and failures the facts script.
    private static Task<ITestBrain> Start(Provider provider) => TestBrain.Create().WithModule<PlaywrightModule>().WithModule<ToolsModule>()
        .ConfigureServices(services => services.AddSingleton<IBrowserSessionProvider>(provider))
        .StartAsync(TestContext.Current.CancellationToken);

    private sealed class Provider : IBrowserSessionProvider
    {
        public string Result { get; init; } = "null";
        public string? Failure { get; init; }
        public List<string> Expressions { get; } = [];
        public Task<IBrowserPageSession> AttachAsync(BrowserAttachment attachment, CancellationToken ct)
            => Task.FromResult<IBrowserPageSession>(new Page(this));
    }

    private sealed class Page(Provider provider) : IBrowserPageSession
    {
        public bool Connected => true;
        public string? Url => "http://127.0.0.1:18081/";
        public string? Title => "FilmCraft";
        public Task<BrowserObservation> NavigateAsync(string url, CancellationToken ct) => SnapshotAsync(ct);
        public Task<BrowserObservation> SnapshotAsync(CancellationToken ct) => Task.FromResult(new BrowserObservation(Url!, Title!, "", []));
        public Task<BrowserObservation> ClickAsync(string selector, CancellationToken ct) => SnapshotAsync(ct);
        public Task<BrowserObservation> FillAsync(string selector, string value, CancellationToken ct) => SnapshotAsync(ct);
        public Task<string> EvaluateAsync(string expression, CancellationToken ct)
        {
            provider.Expressions.Add(expression);
            return provider.Failure is { } failure ? Task.FromException<string>(new InvalidOperationException(failure)) : Task.FromResult(provider.Result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
