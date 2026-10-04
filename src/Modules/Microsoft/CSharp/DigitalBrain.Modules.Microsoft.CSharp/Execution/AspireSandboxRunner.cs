using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Microsoft.Aspire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Microsoft.CSharp;

// Development: every owner shares the csharp-sandbox resource. It starts on demand through IAspire
// and its URL comes from the resource state the AppHost reports, so nothing is wired until needed.
internal sealed class AspireSandboxRunner(HttpClient http, IGrainFactory grains, IOptions<CSharpOptions> options, IConfiguration configuration) : ICSharpRunner
{
    internal static readonly TimeSpan SandboxStartTimeout = TimeSpan.FromMinutes(4);
    internal static readonly TimeSpan StartPollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan InspectProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> StoppedStates = new(["NotStarted", "Exited", "Finished", "FailedToStart", "Unknown"], StringComparer.Ordinal);

    private readonly SandboxRunsApi _api = new(http, static _ => ValueTask.FromResult<string?>(null));

    private IAspire Aspire => grains.GetGrain<IAspire>(options.Value.AspireApplication);

    public async Task StartAsync(CSharpRun run, CancellationToken cancellationToken)
        => await _api.StartAsync(await EnsureSandboxAsync(cancellationToken).ConfigureAwait(false), "", run, cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(string owner, string runId, CancellationToken cancellationToken)
    {
        if (runId.Length == 0 || await FindSandboxAsync(cancellationToken).ConfigureAwait(false) is not { } sandbox) { return; }
        await _api.StopAsync(sandbox, "", runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CSharpRunState> InspectAsync(string owner, string runId, CancellationToken cancellationToken)
    {
        if (runId.Length == 0 || await FindSandboxAsync(cancellationToken).ConfigureAwait(false) is not { } sandbox)
        { return CSharpRunState.Stopped; }
        // A stopped container does not surface as a state change on every runtime, and the
        // endpoint proxy can keep accepting connections for a backend that is gone, so an
        // unreachable or unresponsive sandbox counts as the run being lost, within a bounded probe.
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(InspectProbeTimeout);
        try { return await _api.InspectAsync(sandbox, "", runId, probe.Token).ConfigureAwait(false); }
        catch (HttpRequestException) { return CSharpRunState.Stopped; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return CSharpRunState.Stopped; }
    }

    public async Task<string> LogsAsync(string owner, string runId, int tail, CancellationToken cancellationToken)
        => runId.Length > 0 && await FindSandboxAsync(cancellationToken).ConfigureAwait(false) is { } sandbox
            ? await _api.LogsAsync(sandbox, "", runId, tail, cancellationToken).ConfigureAwait(false)
            : "";

    private async Task<Uri> EnsureSandboxAsync(CancellationToken cancellationToken)
    {
        if (await FindSandboxAsync(cancellationToken).ConfigureAwait(false) is { } running) { return running; }
        var sandbox = (await Aspire.ListResources(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(resource => resource.Name == CSharpSandbox.ResourceName);
        if (sandbox is null || StoppedStates.Contains(sandbox.State))
        {
            await Aspire.StartResource(CSharpSandbox.ResourceName, cancellationToken).ConfigureAwait(false);
        }
        // The sandbox's own health endpoint is the one truth: the bridge's resource report goes
        // stale when the AppHost's resource watch dies, so it only surfaces a failed start.
        var attempts = (int)(SandboxStartTimeout / StartPollInterval);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (await FindSandboxAsync(cancellationToken).ConfigureAwait(false) is { } found) { return found; }
            var reported = (await Aspire.ListResources(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(resource => resource.Name == CSharpSandbox.ResourceName);
            if (reported?.State == "FailedToStart")
            { throw new InvalidOperationException("The C# sandbox failed to start. Check the csharp-sandbox resource in the Aspire dashboard."); }
            await Task.Delay(StartPollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"The C# sandbox did not become healthy within {SandboxStartTimeout.TotalMinutes:F0} minutes.");
    }

    private async Task<Uri?> FindSandboxAsync(CancellationToken cancellationToken)
    {
        var resources = await Aspire.ListResources(cancellationToken).ConfigureAwait(false);
        if (SandboxUrl(resources) is { } reported) { return reported; }
        // The report goes stale when the AppHost's resource watch dies; the allocated endpoint
        // from deployment configuration plus a live health probe stays truthful.
        var address = resources.FirstOrDefault(resource => resource.Name == CSharpSandbox.ResourceName)
                ?.Urls.FirstOrDefault(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? configuration["DigitalBrain:CSharp:SandboxProbeUrl"];
        if (address is not { Length: > 0 }) { return null; }
        var probed = new Uri(address.TrimEnd('/') + "/");
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var health = await http.GetAsync(new Uri(probed, "health"), probe.Token).ConfigureAwait(false);
            return health.IsSuccessStatusCode ? probed : null;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static Uri? SandboxUrl(IReadOnlyList<AspireResource> resources)
        => resources.FirstOrDefault(resource => resource.Name == CSharpSandbox.ResourceName) is { } sandbox
            && IsReady(sandbox.State, sandbox.Health)
            && sandbox.Urls.FirstOrDefault(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) is { } url
                ? new Uri(url.TrimEnd('/') + "/")
                : null;

    private static bool IsReady(string state, string? health) => state == "Running" && health is null or "Healthy";
}
