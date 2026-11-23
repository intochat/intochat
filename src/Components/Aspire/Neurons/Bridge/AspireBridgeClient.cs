using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.Aspire.Bridge;

// Connects to the AppHost's local Unix Domain Socket (zero open TCP ports):
// - Holds one NDJSON event stream to update AspireHostStore and notify active brains via IAspireFeed.
// - Sends short-lived NDJSON requests for ExecuteAsync and ConsoleLogAsync.
internal sealed class AspireBridgeClient(
    AspireHostStore store,
    IConfiguration configuration,
    IServiceProvider services,
    ILogger<AspireBridgeClient>? logger = null)
    : BackgroundService, IAspireRuntimeBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private string? SocketPath => configuration[AspireBridgeProtocol.SocketPathKey];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = SocketPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var backoff = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunEventStreamAsync(path, stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromMilliseconds(250);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Reconnect loop must tolerate AppHost restarts and transient socket errors
            catch (Exception error)
#pragma warning restore CA1031
            {
                store.SetConnected(false);
                logger?.LogDebug(error, "Aspire UDS bridge stream disconnected; reconnecting in {Delay}.", backoff);
            }

            store.SetConnected(false);
            try
            {
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds));
        }

        store.SetConnected(false);
    }

    private async Task RunEventStreamAsync(string path, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var subscribe = JsonSerializer.Serialize(new BridgeCall("subscribe"), JsonOptions);
        await writer.WriteLineAsync(subscribe.AsMemory(), ct).ConfigureAwait(false);

        store.SetConnected(true);
        logger?.LogInformation("Connected to Aspire AppHost UDS bridge.");

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break; // EOF: AppHost closed the socket
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var snapshot = JsonSerializer.Deserialize<BridgeSnapshot>(line, JsonOptions);
            if (snapshot is null)
            {
                continue;
            }

            var changes = store.Apply(snapshot);
            if (changes.Count > 0)
            {
                await NotifyBrainsAsync([.. changes], ct).ConfigureAwait(false);
            }
        }
    }

    private async Task NotifyBrainsAsync(AspireResourceChanged[] changes, CancellationToken ct)
    {
        var edge = services.GetService<EdgeAuthority>();
        foreach (var brain in store.Brains)
        {
            try
            {
                using var scope = edge?.Enter(edge.Module(AspireModule.ManifestName, brain));
                var feed = SiloNeurons.Get<IAspireFeed>(services, new NeuronId(brain, AspireModule.ManifestName, IAspire.KeyPattern));
                await feed.Changed(changes).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One brain notification failure must not drop the bridge stream
            catch (Exception error)
#pragma warning restore CA1031
            {
                OsConsoleLog.For<AspireBridgeClient>().LogSwallowed(error, $"notifying brain {brain} of Aspire resource changes");
            }
        }
    }

    public async Task<BridgeCommandReply> ExecuteAsync(string instanceId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var path = SocketPath;
        if (string.IsNullOrWhiteSpace(path) || !store.Connected)
        {
            return new BridgeCommandReply(false, "Aspire bridge is unavailable.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            var reply = await SendRequestAsync<BridgeCommandReply>(
                path,
                new BridgeCall("command", InstanceId: instanceId, Command: command),
                linked.Token).ConfigureAwait(false);
            return reply ?? new BridgeCommandReply(false, "No reply from AppHost bridge.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BridgeCommandReply(false, "Command timed out waiting for the AppHost.");
        }
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            return new BridgeCommandReply(false, error.Message);
        }
    }

    public async Task<string[]> ConsoleLogAsync(string instanceId, int tail, TimeSpan timeout, CancellationToken ct)
    {
        var path = SocketPath;
        if (string.IsNullOrWhiteSpace(path) || !store.Connected)
        {
            return [];
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            var reply = await SendRequestAsync<BridgeConsoleReply>(
                path,
                new BridgeCall("console", InstanceId: instanceId, Tail: Math.Clamp(tail, 1, 500)),
                linked.Token).ConfigureAwait(false);
            return reply?.Lines ?? [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    private static async Task<TReply?> SendRequestAsync<TReply>(string path, BridgeCall call, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var json = JsonSerializer.Serialize(call, JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line) ? default : JsonSerializer.Deserialize<TReply>(line, JsonOptions);
    }
}
