using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

// Brings every shipped bundle into its publisher's brain at startup: verify its tests once,
// import it when green, skip it when the installed version already matches. A red bundle
// stays out and says why in the log.
internal sealed class ShippedAppPublisher(
    IGrainFactory grains, ITestScriptRunner runner,
    IEnumerable<IShippedAppSource> sources, IConfiguration configuration,
    ILogger<ShippedAppPublisher> logger) : IShippedContentPublisher
{
    // Verifying a shipped app runs real sandbox scripts. A test host declares what it needs:
    // "true" (default) ships everything, "false" nothing, and a comma-separated list of app
    // names ships only those.
    public static readonly string ShipOnStartupKey = ShippedAppsServiceCollectionExtensions.ShipOnStartupKey;

    public string[] Accounts => sources.Select(source => source.Account).Distinct(StringComparer.Ordinal).ToArray();

    public async Task PublishAsync(PublisherBrain publisher, CancellationToken stoppingToken)
    {
        var selection = Selection(configuration[ShipOnStartupKey]);
        if (selection is { Count: 0 })
        {
            logger.LogInformation("Shipped apps stay unpublished: {Key} is off.", ShipOnStartupKey);
            return;
        }
        foreach (var source in sources.Where(source => source.Account == publisher.Username))
        {
            foreach (var bundle in source.Load())
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (selection is not null && !selection.Contains(bundle.Name)) { continue; }
                try { await Ship(publisher, bundle, stoppingToken).WaitAsync(stoppingToken); }
                // Only the host's own shutdown stops shipping; a downstream cancellation or
                // timeout is this bundle's failure, not the process's.
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                { logger.LogError(error, "Shipping {Account}/{App} failed.", publisher.Username, bundle.Name); }
            }
        }
    }

    private async Task Ship(PublisherBrain publisher, AppBundle bundle, CancellationToken cancellationToken)
    {
        var app = grains.GetGrain<IApp>(IApp.Key(publisher.ScopeId, bundle.Name));
        var installed = await app.Read();
        if (installed.Manifest.Title.Length > 0 && installed.Version == bundle.Version) { return; }
        var (verdicts, green) = await AppVerifyJob.RunBundle(grains, runner, bundle,
            IApp.Key(publisher.ScopeId, bundle.Name + AppFiles.VerifyMarker + Guid.NewGuid().ToString("N")), cancellationToken);
        if (!green)
        {
            logger.LogWarning("{Account}/{App} stays out: verification failed: {Failures}", publisher.Username, bundle.Name,
                string.Join("; ", verdicts.Where(verdict => !verdict.Passed).Select(verdict => $"{verdict.Name}: {verdict.Message}")));
            return;
        }
        if (installed.Manifest.Title.Length > 0) { await app.Delete(); }
        await app.Import(bundle);
        await app.RecordVerdicts(verdicts, green: true, bundle.Version);
    }

    // null means every app; an empty set means none.
    private static HashSet<string>? Selection(string? configured) => configured?.Trim().ToLowerInvariant() switch
    {
        null or "" or "true" => null,
        "false" => [],
        var names => [.. names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
    };
}
