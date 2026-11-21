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
internal sealed class WebSearchNeuron(
    IHttpClientFactory? clients = null,
    IOptions<WebModuleOptions>? options = null,
    IBrainProviderKeys? brainKeys = null,
    IModuleIntegrations<WebModule>? integrations = null) : Neuron, IWebSearch
{
    internal const string ProviderVariable = "web.search.provider";
    internal const string SearxngUrlVariable = "web.search.searxng-url";

    private static readonly HttpClient FallbackClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<SearchProvider> Provider()
    {
        var (provider, _) = await ResolveConfigurationAsync().ConfigureAwait(false);
        return provider;
    }

    public async Task<WebResult[]> Search(string query, int count = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var clamped = Math.Clamp(count, 1, 20);
        var (provider, searxngUrl) = await ResolveConfigurationAsync().ConfigureAwait(false);

        switch (provider)
        {
            case SearchProvider.Off:
                throw new InvalidOperationException("Web search is off. Choose a provider in Settings → AI → Web & tools.");

            case SearchProvider.Native:
                throw new InvalidOperationException("Web search is off for tool calls because the chat model searches by itself. Choose a provider in Settings → AI → Web & tools.");

            case SearchProvider.Searxng:
                if (searxngUrl is null || !searxngUrl.IsAbsoluteUri || searxngUrl.Scheme is not ("http" or "https"))
                {
                    throw new InvalidOperationException("SearXNG needs a URL. Configure it in Settings → AI → Web & tools.");
                }

                return await new SearxngSearch(Client(), searxngUrl).Search(query, clamped, CancellationToken.None).ConfigureAwait(false);

            case SearchProvider.Brave:
                return await new BraveSearch(Client(), ct => ResolveKeyAsync(WebIntegrations.BraveId, ct))
                    .Search(query, clamped, CancellationToken.None).ConfigureAwait(false);

            case SearchProvider.Tavily:
                return await new TavilySearch(Client(), ct => ResolveKeyAsync(WebIntegrations.TavilyId, ct))
                    .Search(query, clamped, CancellationToken.None).ConfigureAwait(false);

            default:
                throw new InvalidOperationException("Web search is off. Choose a provider in Settings → AI → Web & tools.");
        }
    }

    private HttpClient Client() => clients?.CreateClient(WebModule.SearchHttpClientName) ?? FallbackClient;

    private async Task<(SearchProvider Provider, Uri? SearxngUrl)> ResolveConfigurationAsync()
    {
        var configured = options?.Value;
        var provider = configured?.Provider ?? SearchProvider.Off;
        var searxngUrl = configured?.SearxngUrl;

        try
        {
            var settings = await Get<IUserSettings>(new NeuronId(Id.Brain, "os.settings", IUserSettings.DefaultKey)).Read().ConfigureAwait(false);
            if (settings.Variables is { Length: > 0 })
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
                var brainKey = await brainKeys.Release(Id.Brain.Value, integrationId, ct).ConfigureAwait(false);
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
                var snapshot = await handle.ReadAsync(ct).ConfigureAwait(false);
                if (snapshot.Status == IntegrationStatus.Ready)
                {
                    var released = await handle.ReleaseAsync(ct).ConfigureAwait(false);
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
