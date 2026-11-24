using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Host.Connections;

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
                ct).ConfigureAwait(true);
        }
    }

    // The connect value is the allow-list; it is kept in the state too, so reads and the build flow's matching need no credential.
    protected override async Task OnConnectedAsync()
    {
        string[] hosts = [];
        await UseCredentialAsync((credential, _) => { hosts = IConnectionHosts.Parse(credential.Value); return Task.FromResult(hosts.Length); }, CancellationToken.None);
        await SaveQuietly(Snapshot with { AllowedHosts = hosts });
    }

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(IConnectionHosts.Parse(credential.Value).Length > 0
            ? ConnectionProbeResult.Connected($"Reads {string.Join(", ", IConnectionHosts.Parse(credential.Value))}.")
            : ConnectionProbeResult.Failing("The allow-list names no host."));

    private static readonly HttpClient FallbackClient = new(HostBoundHttp.Handler(VettedConnect.Dns)) { Timeout = HostBoundHttp.Timeout };

    private HostBoundHttp Http() => new(clients?.CreateClient(HttpModule.ClientName) ?? FallbackClient);

    private static Uri Url(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : throw new HostNotAllowedException(url);
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
