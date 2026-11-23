using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.Aspire.Hosting;

internal sealed record AspireBridgeEndpoint(string SocketPath);

// Run-mode-only Unix Domain Socket bridge hosted inside the AppHost.
// Opens zero TCP ports; streams typed ResourceNotificationService snapshots to connected kernels
// and executes ResourceCommandService / ResourceLoggerService calls on demand.
internal sealed class AspireBridgeServer(
    AspireBridgeEndpoint endpoint,
    ResourceNotificationService notifications,
    ResourceCommandService commands,
    ResourceLoggerService loggers,
    ILogger<AspireBridgeServer>? logger = null)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, BridgeSnapshot> _latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Channel<BridgeSnapshot>> _subscribers = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TryDeleteSocket(endpoint.SocketPath);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(endpoint.SocketPath));
        listener.Listen(backlog: 32);

        logger?.LogInformation("Aspire UDS bridge listening on {SocketPath}.", endpoint.SocketPath);

        var watcher = WatchNotificationsAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            foreach (var channel in _subscribers.Values)
            {
                channel.Writer.TryComplete();
            }

            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            TryDeleteSocket(endpoint.SocketPath);
        }
    }

    private async Task WatchNotificationsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in notifications.WatchAsync(ct).ConfigureAwait(false))
            {
                var snapshot = AspireBridgeMapping.From(ev);
                _latest[snapshot.InstanceId] = snapshot;
                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.Writer.TryWrite(snapshot);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                var call = JsonSerializer.Deserialize<BridgeCall>(line, JsonOptions);
                if (call is null)
                {
                    return;
                }

                switch (call.Op)
                {
                    case "subscribe":
                        await StreamSnapshotsAsync(writer, ct).ConfigureAwait(false);
                        break;

                    case "command":
                        var cmdReply = await RunCommandAsync(call.InstanceId ?? "", call.Command ?? "", ct).ConfigureAwait(false);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(cmdReply, JsonOptions).AsMemory(), ct).ConfigureAwait(false);
                        break;

                    case "console":
                        var lines = await ReadConsoleTailAsync(call.InstanceId ?? "", call.Tail, ct).ConfigureAwait(false);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new BridgeConsoleReply(lines), JsonOptions).AsMemory(), ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031 // Client disconnects or malformed lines must not crash the AppHost bridge
            catch (Exception error)
#pragma warning restore CA1031
            {
                logger?.LogDebug(error, "Aspire UDS bridge client session ended.");
            }
        }
    }

    private async Task StreamSnapshotsAsync(StreamWriter writer, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<BridgeSnapshot>(new UnboundedChannelOptions { SingleReader = true });
        _subscribers[id] = channel;
        try
        {
            foreach (var snapshot in _latest.Values)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot, JsonOptions).AsMemory(), ct).ConfigureAwait(false);
            }

            await foreach (var snapshot in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot, JsonOptions).AsMemory(), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private async Task<BridgeCommandReply> RunCommandAsync(string instanceId, string commandName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(commandName))
        {
            return new BridgeCommandReply(false, "InstanceId and Command are required.");
        }

        try
        {
            var result = await commands.ExecuteCommandAsync(instanceId, commandName, ct).ConfigureAwait(false);
            return result.Success
                ? new BridgeCommandReply(true)
                : new BridgeCommandReply(false, result.Message ?? "Command failed.");
        }
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            return new BridgeCommandReply(false, error.Message);
        }
    }

    private async Task<string[]> ReadConsoleTailAsync(string instanceId, int tail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return [];
        }

        var count = Math.Clamp(tail <= 0 ? 50 : tail, 1, 500);
        var queue = new Queue<string>(count + 1);
        try
        {
            await foreach (var batch in loggers.GetAllAsync(instanceId).WithCancellation(ct).ConfigureAwait(false))
            {
                foreach (var entry in batch)
                {
                    queue.Enqueue(entry.Content);
                    if (queue.Count > count)
                    {
                        queue.Dequeue();
                    }
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }

        return [.. queue];
    }

    private void TryDeleteSocket(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            logger?.LogDebug(error, "Could not delete Aspire UDS bridge socket file {SocketPath}.", path);
        }
    }
}
