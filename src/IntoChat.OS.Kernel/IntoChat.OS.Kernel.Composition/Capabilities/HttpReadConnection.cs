using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Connections.Platform.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Composition.Capabilities;

// The http@1 connector: a brain connects one with its allow-list (auth method "allowed-hosts", value comma-separated hosts or a JSON
// array) and a unit's http slot binds it. Composed with the kernel; OperatingSystemModules lists it beside OrleansModule.
[DigitalBrain.Module(Id)]
public sealed class HttpModule : DigitalBrain.Module
{
    // "web", not "http": the composition README documents its first module by id, and that is Orleans.
    public const string Id = "web";
    public const string AuthMethod = "allowed-hosts";
    public const string ClientName = "IntoChat.OS.Kernel.Http";

    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [Capability.Http.V1],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: [DigitalBrain.Secret.Named(AuthMethod)]);

    public static ConnectorDefinition Connector => new(IHttpConnection.TypeName, "Web (read-only)", [AuthMethod], [])
    {
        Neurons = [typeof(IHttpRead), typeof(IHttpConnection)],
        Signals = [],
    };

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddHttpClient(ClientName, static client => client.Timeout = HostBoundHttp.Timeout)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.TryAddSingleton<IConnectionHosts, KernelConnectionHosts>();
    }
}

[GenerateSerializer, Alias("kernel.http.connection-state")]
public sealed class HttpConnectionState : ConnectionState
{
    [Id(0)] public string[] AllowedHosts { get; set; } = [];
}

// One http@1 connection, keyed {brain}/connection/http/{id}. Every request, and every redirect hop, is held to the allow-list.
[DigitalBrain.Neuron(IHttpConnection.TypeName, State = "http")]
internal sealed class HttpReadConnection(IHttpClientFactory? clients = null) : ConnectionNeuron<HttpConnectionState>, IHttpRead, IHttpConnection
{
    public Task<string[]> AllowedHosts() => Task.FromResult<string[]>([.. Snapshot.AllowedHosts]);

    public Task<JsonElement> GetJson(string url, CancellationToken ct) => Http().GetJson(Url(url), Snapshot.AllowedHosts, ct);

    public Task<string> GetText(string url, CancellationToken ct) => Http().GetText(Url(url), Snapshot.AllowedHosts, ct);

    // The connect value is the allow-list; it is kept in the state too, so reads and the build flow's matching need no credential.
    protected override async Task OnConnectedAsync()
    {
        string[] hosts = [];
        await UseCredentialAsync((credential, _) => { hosts = IConnectionHosts.Parse(credential.Value); return Task.FromResult(hosts.Length); }, CancellationToken.None);
        Snapshot.AllowedHosts = hosts;
        await SaveQuietly(Snapshot);
    }

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(IConnectionHosts.Parse(credential.Value).Length > 0
            ? ConnectionProbeResult.Connected($"Reads {string.Join(", ", IConnectionHosts.Parse(credential.Value))}.")
            : ConnectionProbeResult.Failing("The allow-list names no host."));

    private HostBoundHttp Http() => new(clients?.CreateClient(HttpModule.ClientName) ?? HostBoundHttp.Shared);

    private static Uri Url(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : throw new HostNotAllowedException(url);
}

// GET against an allow-list: HttpHosts.Require on the url and on each redirect hop (at most five; the client never follows one
// itself), 30 seconds, a body of at most 1 MiB.
public sealed class HostBoundHttp(HttpClient client)
{
    public const int MaxRedirects = 5;
    public const int MaxBytes = 1 << 20;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    internal static HttpClient Shared { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout };

    public async Task<JsonElement> GetJson(Uri url, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        var text = await GetText(url, allowed, ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException error) { throw new HttpRequestException($"{url.Host} did not return JSON", error); }
    }

    public async Task<string> GetText(Uri url, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(allowed);
        for (var hop = 0; ; hop++)
        {
            HttpHosts.Require(url, allowed);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                if (hop >= MaxRedirects) { throw new HttpRequestException($"More than {MaxRedirects} redirects from {url.Host}."); }
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await Read(response, ct).ConfigureAwait(false);
        }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<string> Read(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxBytes) { throw new HttpRequestException($"The response is larger than {MaxBytes} bytes."); }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0) { total += read; }
        if (total > MaxBytes) { throw new HttpRequestException($"The response is larger than {MaxBytes} bytes."); }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}

// IConnectionHosts over the brain's own http connections: their AllowedHosts; any other connection keeps no allow-list.
internal sealed class KernelConnectionHosts(IServiceProvider plain) : IConnectionHosts
{
    public async Task<IReadOnlyList<string>?> AllowedHosts(BrainId brain, string connectionKey, CancellationToken ct)
    {
        if (!ConnectionKeys.Is(connectionKey)) { return null; }
        var (owner, connector, _) = ConnectionKeys.Parse(connectionKey);
        if (owner != brain.Value || connector != IHttpConnection.TypeName) { return null; }
        return await SiloNeurons.Get<IHttpConnection>(plain, ConnectionGrains.IdFor(connectionKey)).AllowedHosts().WaitAsync(ct).ConfigureAwait(false);
    }
}
