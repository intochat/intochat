using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Web;

public sealed class WebModuleOnTestOS
{
    [Fact]
    public async Task ModuleRegistersOnTestOS()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(WebModule).Assembly);
        Assert.Equal("webtools", WebModule.ManifestName);
        Assert.Equal(typeof(IWebSearch).Assembly, WebModule.Contracts);
    }

    [Fact]
    public void ManifestDeclaresMeteredHttpClient()
    {
        Assert.Contains(Need.HttpClient("webtools", requestsPerMinute: 60), WebModule.Manifest.Needs);
    }

    [Fact]
    public void ContractsArePlainNeuronContracts()
    {
        Assert.Equal("web.search", PlainNeuronContracts.TypeOf(typeof(IWebSearch)));
        Assert.Equal("web.read", PlainNeuronContracts.TypeOf(typeof(IWebRead)));
        Assert.Equal("web/search", IWebSearch.DefaultKey);
        Assert.Equal("web/read", IWebRead.DefaultKey);
    }

    [Fact]
    public void ValidateRefusesSearxngWithoutValidUrl()
    {
        var missing = new WebModuleOptions { Provider = SearchProvider.Searxng, SearxngUrl = null };
        Assert.Throws<ArgumentException>(missing.Validate);

        var relative = new WebModuleOptions { Provider = SearchProvider.Searxng, SearxngUrl = new Uri("/search", UriKind.Relative) };
        Assert.Throws<ArgumentException>(relative.Validate);

        var nonHttp = new WebModuleOptions { Provider = SearchProvider.Searxng, SearxngUrl = new Uri("ftp://searxng.local") };
        Assert.Throws<ArgumentException>(nonHttp.Validate);

        var badLimit = new WebModuleOptions { MaxMarkdownChars = 0 };
        Assert.Throws<ArgumentException>(badLimit.Validate);

        var valid = new WebModuleOptions { Provider = SearchProvider.Searxng, SearxngUrl = new Uri("http://localhost:8080") };
        valid.Validate();
    }

    [Fact]
    public void ToolSchemasGeneratesWebSearchWithOptionalCountAndWebRead()
    {
        var searchSchemas = ToolSchemas.Of(typeof(IWebSearch));
        var search = Assert.Single(searchSchemas, static s => s.Name == "web_search");
        Assert.Equal("Search", search.Method);
        Assert.Equal("web/search", search.DefaultKey);

        using (var doc = JsonDocument.Parse(search.ParametersJson))
        {
            var props = doc.RootElement.GetProperty("properties");
            Assert.True(props.TryGetProperty("query", out _));
            Assert.True(props.TryGetProperty("count", out _));
            var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(static e => e.GetString()).ToArray();
            Assert.Contains("query", required);
            Assert.DoesNotContain("count", required);
        }

        var readSchemas = ToolSchemas.Of(typeof(IWebRead));
        var read = Assert.Single(readSchemas, static s => s.Name == "web_read");
        Assert.Equal("Read", read.Method);
        Assert.Equal("web/read", read.DefaultKey);
    }
}
