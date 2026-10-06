using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

// Brings every shipped bundle into its publisher's brain at startup: import it when green, skip
// it when the installed version already matches. A bundle compiled into the host is green by
// its build; a folder bundle is verified here, and a red one stays out and says why in the log.
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
                try { await Ship(publisher, source, bundle, stoppingToken).WaitAsync(stoppingToken); }
                // Only the host's own shutdown stops shipping; a downstream cancellation or
                // timeout is this bundle's failure, not the process's.
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                { logger.LogError(error, "Shipping {Account}/{App} failed.", publisher.Username, bundle.Name); }
            }
        }
    }

    private async Task Ship(PublisherBrain publisher, IShippedAppSource source, AppBundle bundle, CancellationToken cancellationToken)
    {
        var app = grains.GetGrain<IApp>(IApp.Key(publisher.ScopeId, bundle.Name));
        var installed = await app.Read();
        var brainVersion = installed.Manifest.Title.Length == 0 ? null : installed.PublishedVersion ?? "0.0.0";
        var order = brainVersion is null ? 1 : AppVersions.Compare(bundle.Version, brainVersion);
        if (order == 0)
        {
            // Same version, different content: the folder changed without a bump and will not ship.
            if (brainVersion is not null && await app.ExportVersion(brainVersion) is { } recorded && !ShippedAppSync.SameContent(bundle, recorded))
            { logger.LogWarning("{Account}/{App} folder differs from its published {Version} without a version bump; bump the version to ship it.", publisher.Username, bundle.Name, brainVersion); }
            return;
        }
        if (order < 0)
        {
            // The publisher moved ahead in the brain; a development folder catches up when it can.
            if (source.Writable) { await ShippedAppSync.WriteIfFastForward(source, app, await app.ExportPublished(), logger); }
            return;
        }
        var (verdicts, green) = source.VerifiedByBuild ? ByBuild(bundle) : await AppVerifyJob.RunBundle(grains, runner, bundle,
                IApp.Key(publisher.ScopeId, bundle.Name + AppFiles.VerifyMarker + Guid.NewGuid().ToString("N")), cancellationToken);
        if (!green)
        {
            logger.LogWarning("{Account}/{App} stays out: verification failed: {Failures}", publisher.Username, bundle.Name,
                string.Join("; ", verdicts.Where(verdict => !verdict.Passed).Select(verdict => $"{verdict.Name}: {verdict.Message}")));
            return;
        }
        // Replace keeps the publisher brain's settings, connections and stored data (B3).
        if (installed.Manifest.Title.Length > 0) { await app.Replace(bundle); }
        else { await app.Import(bundle); }
        await app.RecordVerdicts(verdicts, green: true, bundle.Version);
        await app.Publish();
    }

    // Silence never passes: a bundle that declares no scenario is not green by its build either.
    private static (AppScenarioVerdict[] Verdicts, bool Green) ByBuild(AppBundle bundle)
    {
        var verdicts = AppVerifyJob.Scenarios(bundle.Spec).Order(StringComparer.Ordinal)
            .Select(name => new AppScenarioVerdict(name, true, "Verified by the build.")).ToArray();
        return (verdicts, verdicts.Length > 0);
    }

    // null means every app; an empty set means none.
    private static HashSet<string>? Selection(string? configured) => configured?.Trim().ToLowerInvariant() switch
    {
        null or "" or "true" => null,
        "false" => [],
        var names => [.. names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
    };
}
