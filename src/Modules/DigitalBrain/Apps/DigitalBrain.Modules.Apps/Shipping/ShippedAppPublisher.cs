using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

// Brings every shipped bundle into its publisher's brain at startup: verify its tests once,
// import it when green, skip it when the installed version already matches. A red bundle
// stays out and says why in the log.
internal sealed class ShippedAppPublisher(
    IDigitalBrain brain, ITestScriptRunner runner,
    IEnumerable<IShippedAppSource> sources, IConfiguration configuration,
    ILogger<ShippedAppPublisher> logger) : IShippedContentPublisher
{
    // Verifying a shipped app runs real sandbox scripts. A test host declares what it needs:
    // "true" (default) ships everything, "false" nothing, and a comma-separated list of app
    // names ships only those.
    public static readonly string ShipOnStartupKey = ShippedAppsServiceCollectionExtensions.ShipOnStartupKey;

    public string[] Publishers => sources.Select(source => source.Publisher).Distinct(StringComparer.Ordinal).ToArray();

    public async Task PublishAsync(string publisher, CancellationToken stoppingToken)
    {
        var selection = Selection(configuration[ShipOnStartupKey]);
        if (selection is { Count: 0 })
        {
            logger.LogInformation("Shipped apps stay unpublished: {Key} is off.", ShipOnStartupKey);
            return;
        }
        foreach (var source in sources.Where(source => source.Publisher == publisher))
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
                { logger.LogError(error, "Shipping {Publisher}/{App} failed.", publisher, bundle.Name); }
            }
        }
    }

    private async Task Ship(string publisher, AppBundle bundle, CancellationToken cancellationToken)
    {
        var key = IAppV2.Key(publisher, bundle.Name);
        var app = brain.Get<IAppV2>(key);
        var installed = await app.Read();
        if (installed.Manifest.Title.Length > 0 && installed.Version == bundle.Version) { return; }
        var run = await runner.RunAsync(key, bundle.Files.GetValueOrDefault(AppFiles.TestsPath, ""), cancellationToken);
        var declared = AppVerifyJob.Scenarios(bundle.Spec);
        var green = run.Green
            && declared.All(name => run.Scenarios.Any(verdict => verdict.Name == name && verdict.Passed))
            && (declared.Count == 0 || run.Scenarios.All(verdict => declared.Contains(verdict.Name)));
        if (!green)
        {
            logger.LogWarning("{Publisher}/{App} stays out: verification failed.", publisher, bundle.Name);
            return;
        }
        if (installed.Manifest.Title.Length > 0) { await app.Delete(); }
        await app.Import(bundle);
        await app.RecordVerdicts(run.Scenarios, green: true);
    }

    // null means every app; an empty set means none.
    private static HashSet<string>? Selection(string? configured) => configured?.Trim().ToLowerInvariant() switch
    {
        null or "" or "true" => null,
        "false" => [],
        var names => [.. names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
    };
}
