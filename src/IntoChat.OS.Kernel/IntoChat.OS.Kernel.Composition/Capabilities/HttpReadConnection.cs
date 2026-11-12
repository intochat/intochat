using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Connections.Platform.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Composition.Capabilities;

// The http@1 connector: a brain connects one with its allow-list (auth method "allowed-hosts", value comma-separated hosts or a JSON
// array) and a unit's http slot binds it. Composed with the kernel; OperatingSystemModules lists it beside OrleansModule.
[Module(Id)]
public sealed class HttpModule : Module
{
    // "web", not "http": the composition README documents its first module by id, and that is Orleans.
    public const string Id = "web";
    public const string AuthMethod = "allowed-hosts";
    public const string ClientName = "IntoChat.OS.Kernel.Http";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [Capability.Http.V1],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: [Secret.Named(AuthMethod)]);

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
            .ConfigurePrimaryHttpMessageHandler(static () => HostBoundHttp.Handler(VettedConnect.Dns));
        services.TryAddSingleton<IConnectionHosts, KernelConnectionHosts>();
    }
}

[GenerateSerializer, Alias("kernel.http.connection-state")]
public sealed record HttpConnectionState : ConnectionState
{
    [Id(0)] public string[] AllowedHosts { get; set; } = [];
}

// One http@1 connection, keyed {brain}/connection/http/{id}. Every request, and every redirect hop, is held to the allow-list.
[Neuron(IHttpConnection.TypeName, State = "http")]
internal sealed class HttpReadConnection(
    IHttpClientFactory? clients = null,
    DigitalBrain.Capabilities.Compute.ICostMeter? meter = null) : ConnectionNeuron<HttpConnectionState>, IHttpRead, IHttpConnection
{
    public Task<string[]> AllowedHosts() => Task.FromResult<string[]>([.. Snapshot.AllowedHosts]);

    public async Task<JsonElement> GetJson(string url, CancellationToken ct)
    {
        var http = Http();
        var json = await http.GetJson(Url(url), Snapshot.AllowedHosts, ct);
        await RecordMeterAsync(http.TotalBytesRead, ct);
        return json;
    }

    public async Task<string> GetText(string url, CancellationToken ct)
    {
        var http = Http();
        var text = await http.GetText(Url(url), Snapshot.AllowedHosts, ct);
        await RecordMeterAsync(http.TotalBytesRead, ct);
        return text;
    }

    private async ValueTask RecordMeterAsync(long bytesRead, CancellationToken ct)
    {
        if (meter is not null)
        {
            await meter.RecordAsync(
                DigitalBrain.Capabilities.Compute.CostKind.Http,
                IHttpConnection.TypeName,
                bytesRead,
                "bytes",
                DigitalBrain.Capabilities.Compute.Funding.Local,
                Guid.NewGuid().ToString("N"),
                ct).ConfigureAwait(false);
        }
    }

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
// itself), 30 seconds, a body of at most 1 MiB. A literal IP is read only when the allow-list names that IP itself; the handler
// connects only to a public address the host resolves to (VettedConnect).
public sealed class HostBoundHttp(HttpClient client)
{
    public const int MaxRedirects = 5;
    public const int MaxBytes = 1 << 20;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private long _totalRequests;
    private long _totalBytesRead;

    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    public long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);

    internal static HttpClient Shared { get; } = new(Handler(VettedConnect.Dns)) { Timeout = Timeout };

    // No redirect followed by the client, no proxy (the vetted address is the one connected to), and every connection vetted.
    public static SocketsHttpHandler Handler(VettedConnect.Resolver resolve)
        => new() { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = new VettedConnect(resolve).Connect };

    public async Task<JsonElement> GetJson(Uri url, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        var text = await GetText(url, allowed, ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        // Named by the host the unit asked for, never a host a redirect led to.
        catch (JsonException error) { throw new HttpRequestException($"{url.Host} did not return JSON", error); }
    }

    public async Task<string> GetText(Uri url, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(allowed);
        var asked = url.Host;
        for (var hop = 0; ; hop++)
        {
            RequireNamedIfLiteral(url, allowed);
            HttpHosts.Require(url, allowed);
            Interlocked.Increment(ref _totalRequests);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                if (hop >= MaxRedirects) { throw new HttpRequestException($"More than {MaxRedirects} redirects from {asked}."); }
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await Read(response, ct).ConfigureAwait(false);
        }
    }

    // An IP in the url passes only when the allow-list names exactly that IP; a host pattern never covers one.
    private static void RequireNamedIfLiteral(Uri url, IReadOnlyList<string> allowed)
    {
        if (url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)) { return; }
        var host = url.IdnHost.Trim('[', ']');
        if (!allowed.Any(entry => string.Equals(entry.Trim().Trim('[', ']'), host, StringComparison.OrdinalIgnoreCase)))
        { throw new HostNotAllowedException(url.Host); }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private async Task<string> Read(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxBytes) { throw new HttpRequestException($"The response is larger than {MaxBytes} bytes."); }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0) { total += read; }
        if (total > MaxBytes) { throw new HttpRequestException($"The response is larger than {MaxBytes} bytes."); }
        Interlocked.Add(ref _totalBytesRead, total);
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}

// The handler's connect: resolves the host, keeps only public addresses (never loopback, link-local, private, unique-local,
// unspecified or multicast, IPv4-mapped included) and connects to the first that answers. A host with none is refused before any
// socket opens, so an allowed name that resolves inward cannot reach the host's own network.
public sealed class VettedConnect(VettedConnect.Resolver resolve)
{
    public delegate Task<IPAddress[]> Resolver(string host, CancellationToken ct);

    public static readonly Resolver Dns = static (host, ct) => System.Net.Dns.GetHostAddressesAsync(host, ct);

    public async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await resolve(host, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in Public(host, addresses))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) { socket.Dispose(); last = error; }
        }
        throw new HttpRequestException($"Could not connect to {host}.", last);
    }

    // The public addresses of host, in resolver order; none is a refusal.
    public static IPAddress[] Public(string host, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var vetted = addresses.Where(IsPublic).ToArray();
        return vetted.Length > 0 ? vetted : throw new HostNotAllowedException(host);
    }

    public static bool IsPublic(IPAddress address) => HttpHosts.IsPublic(address);
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
