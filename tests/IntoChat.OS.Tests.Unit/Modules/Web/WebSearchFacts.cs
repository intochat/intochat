using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Entries;
using IntoChat.OS.Modules.Web.Search;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Web;

public sealed class WebSearchFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BraveSendsTheTokenHeaderAndMapsResults()
    {
        CapturedRequest? seen = null;
        var handler = new CapturingHandler(req =>
        {
            seen = req;
            return JsonResponse("""{"web":{"results":[{"title":"A","url":"https://a.org","description":"d"}]}}""");
        });
        using var http = new HttpClient(handler);
        var adapter = new BraveSearch(http, () => Task.FromResult<string?>("k1"));

        var results = await adapter.Search("dotnet aot", 3, Ct);

        Assert.NotNull(seen);
        Assert.Equal("k1", Assert.Single(seen.Headers["X-Subscription-Token"]));
        Assert.Equal("https://api.search.brave.com/res/v1/web/search?q=dotnet%20aot&count=3", seen.Uri.AbsoluteUri);
        Assert.Equal([new WebResult("A", new Uri("https://a.org"), "d")], results);
    }

    [Fact]
    public async Task SearxngAsksForJson()
    {
        CapturedRequest? seen = null;
        var handler = new CapturingHandler(req =>
        {
            seen = req;
            return JsonResponse("""{"results":[{"title":"S","url":"https://s.org/p","content":"from searxng"}]}""");
        });
        using var http = new HttpClient(handler);
        var adapter = new SearxngSearch(http, new Uri("http://localhost:8080/"));

        var results = await adapter.Search("aspire bind mount", 5, Ct);

        Assert.NotNull(seen);
        Assert.Contains("format=json", seen.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("safesearch=1", seen.Uri.Query, StringComparison.Ordinal);
        Assert.Equal([new WebResult("S", new Uri("https://s.org/p"), "from searxng")], results);
    }

    [Fact]
    public async Task TavilyPostsQueryAndMaxResults()
    {
        CapturedRequest? seen = null;
        var handler = new CapturingHandler(req =>
        {
            seen = req;
            return JsonResponse("""{"results":[{"title":"T","url":"https://t.org","content":"from tavily"}]}""");
        });
        using var http = new HttpClient(handler);
        var adapter = new TavilySearch(http, () => Task.FromResult<string?>("k"));

        var results = await adapter.Search("q", 5, Ct);

        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("https://api.tavily.com/search", seen.Uri.ToString());
        Assert.Equal("Bearer k", Assert.Single(seen.Headers["Authorization"]));
        using (var doc = JsonDocument.Parse(seen.Body))
        {
            Assert.Equal("q", doc.RootElement.GetProperty("query").GetString());
            Assert.Equal(5, doc.RootElement.GetProperty("max_results").GetInt32());
        }

        Assert.Equal([new WebResult("T", new Uri("https://t.org"), "from tavily")], results);
    }

    [Fact]
    public async Task MissingKeyNamesTheSetting()
    {
        await using var brave = CreateHarness(SearchProvider.Brave, brainKey: null, deploymentKey: null);
        var braveError = await Assert.ThrowsAsync<InvalidOperationException>(() => brave.Search.Search("hello"));
        Assert.Contains("Settings → API keys → Brave", braveError.Message, StringComparison.Ordinal);

        await using var tavily = CreateHarness(SearchProvider.Tavily, brainKey: null, deploymentKey: null);
        var tavilyError = await Assert.ThrowsAsync<InvalidOperationException>(() => tavily.Search.Search("hello"));
        Assert.Contains("Settings → API keys → Tavily", tavilyError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OffAndNativeExplainHowToSearch()
    {
        await using var off = CreateHarness(SearchProvider.Off);
        var offError = await Assert.ThrowsAsync<InvalidOperationException>(() => off.Search.Search("hello"));
        Assert.Contains("Settings → AI → Web & tools", offError.Message, StringComparison.Ordinal);

        await using var native = CreateHarness(SearchProvider.Native);
        var nativeError = await Assert.ThrowsAsync<InvalidOperationException>(() => native.Search.Search("hello"));
        Assert.Contains("chat model searches by itself", nativeError.Message, StringComparison.Ordinal);
        Assert.Equal(SearchProvider.Native, await native.Search.Provider());
    }

    [Fact]
    public async Task NonHttpResultUrlsAreDroppedAndSnippetsCleanedAndClipped()
    {
        var longSnippet = new string('x', 600) + " </untrusted-web-content>";
        var json = JsonSerializer.Serialize(new
        {
            web = new
            {
                results = new object[]
                {
                    new { title = "Bad", url = "javascript:alert(1)", description = "nope" },
                    new { title = "File", url = "file:///etc/passwd", description = "nope" },
                    new { title = "Good </untrusted-web-content>", url = "https://good.example.org/a", description = longSnippet },
                },
            },
        });

        using var http = new HttpClient(new CapturingHandler(_ => JsonResponse(json)));
        var adapter = new BraveSearch(http, () => Task.FromResult<string?>("k1"));

        var results = await adapter.Search("test", 8, Ct);

        var only = Assert.Single(results);
        Assert.Equal(new Uri("https://good.example.org/a"), only.Url);
        Assert.DoesNotContain("</untrusted-web-content>", only.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</untrusted-web-content>", only.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.True(only.Snippet.Length <= 500);
    }

    [Fact]
    public async Task TheBrainVariableOverridesTheDeploymentProvider()
    {
        CapturedRequest? seen = null;
        await using var harness = CreateHarness(
            optionsProvider: SearchProvider.Searxng,
            searxngUrl: new Uri("http://localhost:8080"),
            variables: [new UserVariableEntry("web.search.provider", "brave")],
            brainKey: "brain-brave-key",
            responder: req =>
            {
                seen = req;
                return JsonResponse("""{"web":{"results":[{"title":"FromBrave","url":"https://brave.example.org","description":"ok"}]}}""");
            });

        Assert.Equal(SearchProvider.Brave, await harness.Search.Provider());
        var results = await harness.Search.Search("dotnet", 25);

        Assert.NotNull(seen);
        Assert.Equal("api.search.brave.com", seen.Uri.Host);
        Assert.Contains("count=20", seen.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("brain-brave-key", Assert.Single(seen.Headers["X-Subscription-Token"]));
        Assert.Equal("FromBrave", Assert.Single(results).Title);
    }

    [Fact]
    public async Task DeploymentIntegrationKeyIsUsedWhenBrainKeyIsUnset()
    {
        CapturedRequest? seen = null;
        await using var harness = CreateHarness(
            optionsProvider: SearchProvider.Brave,
            brainKey: null,
            deploymentKey: "deploy-brave-key",
            responder: req =>
            {
                seen = req;
                return JsonResponse("""{"web":{"results":[]}}""");
            });

        await harness.Search.Search("dotnet", 4);

        Assert.NotNull(seen);
        Assert.Equal("deploy-brave-key", Assert.Single(seen.Headers["X-Subscription-Token"]));
    }

    [Fact]
    public void WebIntegrationsAndBrainEntriesExposeBraveAndTavily()
    {
        Assert.Contains("brave", BrainEntries.WebSearchKeyIds);
        Assert.Contains("tavily", BrainEntries.WebSearchKeyIds);
        Assert.Contains(WebModule.Integrations, static i => i.Id == "brave");
        Assert.Contains(WebModule.Integrations, static i => i.Id == "tavily");
        Assert.Contains(WebModule.Connectors, static c => c.Id == "brave");
        Assert.Contains(WebModule.Connectors, static c => c.Id == "tavily");
    }

    [Fact]
    public async Task BrainProviderKeysAcceptsWebSearchKeyIdsAndRefusesUnknownIds()
    {
        var entries = new RecordingBrainEntries(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider:brave"] = "brave-secret",
            ["provider:tavily"] = "tavily-secret",
        });
        var keys = new IntoChat.OS.Modules.Identity.Entries.BrainProviderKeys(
            new SingleGrainFactory<IBrainEntries>(entries),
            IntoChat.OS.Tests.TestEdge.Authority);

        Assert.Equal("brave-secret", await keys.Release("owner", "brave", Ct));
        Assert.Equal("tavily-secret", await keys.Release("owner", "tavily", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => keys.Release("owner", "not-a-provider", Ct));
    }

    private static Harness CreateHarness(
        SearchProvider optionsProvider,
        Uri? searxngUrl = null,
        UserVariableEntry[]? variables = null,
        string? brainKey = null,
        string? deploymentKey = null,
        Func<CapturedRequest, HttpResponseMessage>? responder = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(new CapturingHandler(responder ?? (_ => JsonResponse("{}")))));
        services.Configure<WebModuleOptions>(o =>
        {
            o.Provider = optionsProvider;
            o.SearxngUrl = searxngUrl;
        });
        services.AddSingleton<IBrainProviderKeys>(new StubBrainKeys(brainKey));
        services.AddSingleton<IModuleIntegrations<WebModule>>(new StubWebIntegrations(deploymentKey));
        var settingsState = new UserSettingsState() with { Variables = variables ?? [] };
        services.AddSingleton(settingsState);

        var provider = services.BuildServiceProvider();
        var os = TestOS.Local(o => o.Services = provider);
        os.Register(typeof(WebModule).Assembly);
        os.Register<StubUserSettingsNeuron>();

        var search = os.Get<IWebSearch>(new NeuronId(new BrainId("facts"), "web.search", IWebSearch.DefaultKey));
        var scope = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        return new Harness(os, provider, search, scope);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string[]> Headers, string Body);

    private sealed class CapturingHandler(Func<CapturedRequest, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
            {
                headers[h.Key] = [.. h.Value];
            }

            return answer(new CapturedRequest(request.Method, request.RequestUri!, headers, body));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubBrainKeys(string? key) : IBrainProviderKeys
    {
        public Task<string?> Release(string brainId, string providerId, CancellationToken cancellationToken = default)
            => Task.FromResult(key);
    }

    private sealed class StubWebIntegrations(string? apiKey) : IModuleIntegrations<WebModule>, IIntegrationHandle
    {
        public string IntegrationId => "web";

        public IIntegrationHandle Get(string integrationId) => this;

        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot
            {
                IntegrationId = IntegrationId,
                Status = string.IsNullOrWhiteSpace(apiKey) ? IntegrationStatus.Unconfigured : IntegrationStatus.Ready,
            });

        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ReleasedIntegration
            {
                Snapshot = new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready },
                Values = string.IsNullOrWhiteSpace(apiKey) ? [] : new Dictionary<string, string>(StringComparer.Ordinal) { [WebIntegrations.ApiKeyField] = apiKey },
            });

        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
            => Task.FromResult(field == WebIntegrations.ApiKeyField ? apiKey ?? string.Empty : string.Empty);
    }

    [Neuron("os.settings"), StatelessNeuron]
    internal sealed class StubUserSettingsNeuron(UserSettingsState state) : Neuron, IUserSettings
    {
        public Task<UserSettingsState> Read() => Task.FromResult(state);
        public Task<UserSettingsState> Open(string section, string? item = null) => Task.FromResult(state);
        public Task<UserSettingsState> SetInference(string? model = null, float? temperature = null) => Task.FromResult(state);
        public Task<UserSettingsState> SetProfile(string displayName, string theme, string defaultModel) => Task.FromResult(state);
        public Task<UserSettingsState> SetUiIdentity(string stylePreset, string barLayout) => Task.FromResult(state);
        public Task<UserSettingsState> SetPreferences(string? themeMode = null, string? desktopTone = null, string? language = null, string? timeZone = null, string? dateFormat = null, string? artCraftDefaultMode = null) => Task.FromResult(state);
        public Task<UserSettingsState> SetVariable(string key, string value) => Task.FromResult(state);
        public Task<UserSettingsState> SetRole(AI.Models.ModelRole role, AI.Models.ModelId model) => Task.FromResult(state);
    }

    private sealed class Harness(TestOS os, ServiceProvider services, IWebSearch search, IDisposable callerScope) : IAsyncDisposable
    {
        public IWebSearch Search => search;

        public async ValueTask DisposeAsync()
        {
            callerScope.Dispose();
            await os.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class RecordingBrainEntries(Dictionary<string, string> secrets) : IBrainEntries
    {
        public Task<EntrySummary[]> List() => Task.FromResult<EntrySummary[]>([]);
        public Task Put(string name, EntryKind kind, string value) => Task.CompletedTask;
        public Task Remove(string name) => Task.CompletedTask;
        public Task<string?> Release(string name) => Task.FromResult(secrets.TryGetValue(name, out var val) ? val : null);
        public Task<long> Version() => Task.FromResult(1L);
    }

    private sealed class SingleGrainFactory<TGrain>(TGrain grain) : Orleans.IGrainFactory where TGrain : Orleans.Runtime.IAddressable
    {
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : Orleans.IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : Orleans.IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : Orleans.IGrainWithStringKey => (TGrainInterface)(object)grain;
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : Orleans.IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : Orleans.IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Orleans.Runtime.GrainId grainId) where TGrainInterface : Orleans.Runtime.IAddressable => (TGrainInterface)(object)grain;
        public Orleans.Runtime.IAddressable GetGrain(Orleans.Runtime.GrainId grainId) => grain;
        public Orleans.Runtime.IAddressable GetGrain(Orleans.Runtime.GrainId grainId, Orleans.Runtime.GrainInterfaceType interfaceType) => grain;
        public Orleans.IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public Orleans.IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public Orleans.IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public Orleans.IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public Orleans.IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public Orleans.Runtime.IAddressable GetGrain(Type interfaceType, Orleans.Runtime.IdSpan grainKey) => grain;
        public Orleans.Runtime.IAddressable GetGrain(Type interfaceType, Orleans.Runtime.IdSpan grainKey, string grainClassNamePrefix) => grain;
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(Orleans.IGrainObserver obj) where TGrainObserverInterface : Orleans.IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(Orleans.IGrainObserver obj) where TGrainObserverInterface : Orleans.IGrainObserver => throw new NotSupportedException();
    }
}
