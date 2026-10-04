using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Microsoft.Aspire;

// Runs inside the AppHost: pushes every resource state change to the brain and executes the
// resource commands the brain streams back. The brain resends nothing, so after each (re)connect
// the latest state of every resource is pushed again.
internal sealed class AspireBridgeClient(
    string bridgeKey,
    DistributedApplicationModel model,
    ResourceNotificationService notifications,
    ResourceCommandService commands,
    ILogger<AspireBridgeClient> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AspireResource> _latest = new(StringComparer.Ordinal);
    private readonly Channel<AspireResource> _outbox = Channel.CreateUnbounded<AspireResource>(new() { SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hosts = SiloHosts.Find(model.Resources).OfType<IResourceWithEndpoints>().ToArray();
        if (hosts.Length == 0)
        {
            logger.LogWarning("No silo host with an http endpoint is declared; the Aspire bridge stays idle.");
            return;
        }

        try
        {
            var host = await WaitForAnyRunningAsync(hosts, stoppingToken).ConfigureAwait(false);
            using var brain = new HttpClient
            {
                BaseAddress = await ResolveSiloUrlAsync(host.GetEndpoint(SiloHosts.HttpEndpointName), stoppingToken).ConfigureAwait(false),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            brain.DefaultRequestHeaders.Add(AspireBridgeRoutes.KeyHeader, bridgeKey);
            await Task.WhenAll(
                WatchResourcesAsync(stoppingToken),
                PushResourcesAsync(brain, stoppingToken),
                ServeCommandsAsync(brain, stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // The bridge is plumbing: without it, brain-issued resource commands time out with
            // "No Aspire AppHost is connected". It must never stop the AppHost itself (the
            // hosting default for a failed BackgroundService is StopHost).
            logger.LogError(error, "The Aspire bridge stopped; the brain cannot command resources.");
        }
    }

    // The endpoint proxy does not hold a streaming response open, which would turn the command
    // stream into a two-second reconnect loop; the AppHost dials the silo's own port directly.
    // A dynamically allocated endpoint resolves TargetPort to a DCP placeholder, not a number,
    // so anything non-numeric falls back to the proxied URL, which the heartbeat keeps alive.
    private static async Task<Uri> ResolveSiloUrlAsync(EndpointReference endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var port = await ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.TargetPort)}")
                .GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (int.TryParse(port, out var number) && number is > 0 and <= 65535)
            {
                return new Uri($"http://localhost:{number}");
            }
        }
        catch (InvalidOperationException) { }
        return new Uri(endpoint.Url);
    }

    private async Task<IResourceWithEndpoints> WaitForAnyRunningAsync(IReadOnlyList<IResourceWithEndpoints> hosts, CancellationToken stoppingToken)
    {
        using var anyRunning = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var started = hosts.Select(async host =>
        {
            await notifications.WaitForResourceAsync(host.Name, KnownResourceStates.Running, anyRunning.Token).ConfigureAwait(false);
            return host;
        }).ToArray();
        var finished = await Task.WhenAny(started).ConfigureAwait(false);
        anyRunning.Cancel();
        foreach (var task in started)
        { _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); }
        return await finished.ConfigureAwait(false);
    }

    private async Task WatchResourcesAsync(CancellationToken stoppingToken)
    {
        await foreach (var change in notifications.WatchAsync(stoppingToken).ConfigureAwait(false))
        {
            var snapshot = change.Snapshot;
            var resource = new AspireResource(change.Resource.Name, snapshot.ResourceType, snapshot.State?.Text ?? "Unknown",
                snapshot.HealthStatus?.ToString(), [.. snapshot.Urls.Select(url => url.Url)]);
            lock (_gate)
            {
                if (_latest.TryGetValue(resource.Name, out var known))
                {
                    // A null health is "being evaluated", not a transition: health checks flap
                    // Healthy->null on every probe, and reposting each flap floods the brain
                    // with Report calls (the idle trace budget measured 87 spans a minute).
                    if (resource.Health is null && known.Health is not null && known.State == resource.State
                        && known.Urls.SequenceEqual(resource.Urls, StringComparer.Ordinal)) { continue; }
                    if (known.State == resource.State && known.Health == resource.Health
                        && known.Urls.SequenceEqual(resource.Urls, StringComparer.Ordinal)) { continue; }
                }
                _latest[resource.Name] = resource;
            }
            await _outbox.Writer.WriteAsync(resource, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PushResourcesAsync(HttpClient brain, CancellationToken stoppingToken)
    {
        await foreach (var resource in _outbox.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    using var response = await brain.PostAsJsonAsync(AspireBridgeRoutes.Resources, resource, stoppingToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    break;
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug(error, "Brain is not accepting resource state yet; retrying.");
                    await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeCommandsAsync(HttpClient brain, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await brain.GetAsync(AspireBridgeRoutes.Commands, HttpCompletionOption.ResponseHeadersRead, stoppingToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await SyncLatestAsync(brain, stoppingToken).ConfigureAwait(false);
                await using var stream = await response.Content.ReadAsStreamAsync(stoppingToken).ConfigureAwait(false);
                var parser = SseParser.Create(stream, static (_, data) => JsonSerializer.Deserialize<AspireBridgeCommand>(data, JsonSerializerOptions.Web)!);
                await foreach (var item in parser.EnumerateAsync(stoppingToken).ConfigureAwait(false))
                {
                    // Heartbeats keep the proxied stream alive and carry no command.
                    if (item.Data.Id == Guid.Empty) { continue; }
                    _ = ExecuteCommandAsync(brain, item.Data, stoppingToken);
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(error, "Aspire bridge disconnected from the brain; reconnecting.");
            }
            await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    // One request carries the whole post-connect state sync, so an idle minute is not spent
    // on a span per resource.
    private async Task SyncLatestAsync(HttpClient brain, CancellationToken stoppingToken)
    {
        AspireResource[] sync;
        lock (_gate) { sync = [.. _latest.Values]; }
        if (sync.Length == 0) { return; }
        while (true)
        {
            try
            {
                using var response = await brain.PostAsJsonAsync(AspireBridgeRoutes.ResourceSync, sync, stoppingToken).ConfigureAwait(false);
                // A refusal is a bug on the silo, not a brain that is still booting; a silent
                // retry here once hid an enforcement denial behind four-minute command timeouts.
                // Surface it and move on so the command stream still gets served.
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("The brain refused the resource sync ({Status}): {Detail}",
                        (int)response.StatusCode, await response.Content.ReadAsStringAsync(stoppingToken).ConfigureAwait(false));
                }
                return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(error, "Brain is not accepting the resource sync yet; retrying.");
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteCommandAsync(HttpClient brain, AspireBridgeCommand command, CancellationToken stoppingToken)
    {
        logger.LogInformation("Executing {Command} on {Resource} for the brain.", command.Command, command.Resource);
        AspireBridgeCommandResult result;
        try
        {
            var executed = await commands.ExecuteCommandAsync(command.Resource, command.Command, stoppingToken).ConfigureAwait(false);
            result = new(executed.Success, executed.Message);
            logger.LogInformation("{Command} on {Resource} finished: {Outcome}.", command.Command, command.Resource,
                executed.Success ? "success" : executed.Message);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            result = new(false, error.Message);
        }
        try
        {
            using var response = await brain.PostAsJsonAsync(AspireBridgeRoutes.CommandResult(command.Id), result, stoppingToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "The brain did not accept the result of {Command} on {Resource}.", command.Command, command.Resource);
        }
    }
}
