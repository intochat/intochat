using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Entries;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Web.Search;

[Neuron("web.search"), StatelessNeuron]
internal sealed partial class WebSearchNeuron(
    IHttpClientFactory? clients = null,
    IOptions<WebModuleOptions>? options = null,
    IBrainProviderKeys? brainKeys = null,
    IModuleIntegrations<WebModule>? integrations = null,
    IEnumerable<ISearchAdapter>? adapters = null,
    IEnumerable<SearchAdapterRegistration>? adapterRegistrations = null) : Neuron, IWebSearch
{
    [DigitalBrain.Slot(Key = IUserSettings.DefaultKey, Required = false)]
    private partial IUserSettings? UserSettings { get; }
    internal const string ProviderVariable = "web.search.provider";
    internal const string SearxngUrlVariable = "web.search.searxng-url";

    private static readonly HttpClient FallbackClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    internal static HttpClient CreateClient(IHttpClientFactory? clients)
        => clients?.CreateClient(WebModule.SearchHttpClientName) ?? FallbackClient;

    public async Task<SearchProvider> Provider()
    {
        var (provider, _) = await ResolveConfigurationAsync().ConfigureAwait(true);
        return provider;
    }

    public async Task<WebResult[]> Search(string query, int count = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var clamped = Math.Clamp(count, 1, 20);
        var (provider, searxngUrl) = await ResolveConfigurationAsync().ConfigureAwait(true);

        switch (provider)
        {
            case SearchProvider.Off:
                throw new InvalidOperationException("Web search is off. Choose a provider in Settings → AI → Web & tools.");

            case SearchProvider.Native:
                throw new InvalidOperationException("Web search is off for tool calls because the chat model searches by itself. Choose a provider in Settings → AI → Web & tools.");

            default:
                var adapter = ResolveAdapter(provider, searxngUrl);
                return await adapter.Search(query, clamped, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private ISearchAdapter ResolveAdapter(SearchProvider provider, Uri? searxngUrl)
    {
        var direct = adapters?.FirstOrDefault(a => a.Provider == provider);
        if (direct is not null)
        {
            return direct;
        }

        var context = new SearchAdapterContext(searxngUrl, ResolveKeyAsync);
        var registration = adapterRegistrations?.FirstOrDefault(r => r.Provider == provider)
            ?? DefaultRegistration(provider);
        if (registration is not null)
        {
            return registration.Create(context);
        }

        throw new InvalidOperationException("Web search is off. Choose a provider in Settings → AI → Web & tools.");
    }

    private SearchAdapterRegistration? DefaultRegistration(SearchProvider provider) => provider switch
    {
        SearchProvider.Searxng => new SearxngSearchRegistration(clients),
        SearchProvider.Brave => new BraveSearchRegistration(clients),
        SearchProvider.Tavily => new TavilySearchRegistration(clients),
        _ => null,
    };

    private async Task<(SearchProvider Provider, Uri? SearxngUrl)> ResolveConfigurationAsync()
    {
        var configured = options?.Value;
        var provider = configured?.Provider ?? SearchProvider.Off;
        var searxngUrl = configured?.SearxngUrl;

        try
        {
            var settings = UserSettings is { } settingsNeuron ? await settingsNeuron.Read().ConfigureAwait(true) : null;
            if (settings?.Variables is { Length: > 0 })
            {
                foreach (var entry in settings.Variables)
                {
                    if (string.Equals(entry.Key, ProviderVariable, StringComparison.Ordinal)
                        && Enum.TryParse<SearchProvider>(entry.Value?.Trim(), ignoreCase: true, out var overrideProvider)
                        && Enum.IsDefined(overrideProvider))
                    {
                        provider = overrideProvider;
                    }
                    else if (string.Equals(entry.Key, SearxngUrlVariable, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(entry.Value)
                        && Uri.TryCreate(entry.Value.Trim(), UriKind.Absolute, out var parsedUrl)
                        && parsedUrl.Scheme is "http" or "https")
                    {
                        searxngUrl = parsedUrl;
                    }
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<WebSearchNeuron>().LogSwallowed(error, "reading user settings variables for web search; falling back to module options");
        }

        return (provider, searxngUrl);
    }

    private async Task<string?> ResolveKeyAsync(string integrationId, CancellationToken ct)
    {
        if (brainKeys is not null)
        {
            try
            {
                var brainKey = await brainKeys.Release(Id.Brain.Value, integrationId, ct).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(brainKey))
                {
                    return brainKey;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<WebSearchNeuron>().LogSwallowed(error, $"releasing brain key for web search integration '{integrationId}'");
            }
        }

        if (integrations is not null)
        {
            try
            {
                var handle = integrations.Get(integrationId);
                var snapshot = await handle.ReadAsync(ct).ConfigureAwait(true);
                if (snapshot.Status == IntegrationStatus.Ready)
                {
                    var released = await handle.ReleaseAsync(ct).ConfigureAwait(true);
                    if (released.Values.TryGetValue(WebIntegrations.ApiKeyField, out var key) && !string.IsNullOrWhiteSpace(key))
                    {
                        return key;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<WebSearchNeuron>().LogSwallowed(error, $"releasing deployment key for web search integration '{integrationId}'");
            }
        }

        return null;
    }
}
