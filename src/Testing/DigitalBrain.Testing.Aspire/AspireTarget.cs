using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Testing.Aspire;

// The consumer's AppHost behind ITestBrain: a fresh cluster on non-persistent storage per brain.
public sealed class AspireTarget<TAppHost> : ITestTarget where TAppHost : class
{
    public async Task<ITestBrain> StartAsync(TestBrainPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Modules.Count > 0) { throw new NotSupportedException("The AppHost owns composition; use WithOptions/WithIntegration on an Aspire target."); }
        if (plan.Services is not null) { throw new NotSupportedException("ConfigureServices is in-process only; put a fake behind WithIntegration."); }
        if (plan.Client is not null || plan.Silo is not null) { throw new NotSupportedException("ConfigureSilo and ConfigureClient reach the in-process host only."); }
        var integration = IntegrationSchema.SectionName + ":";
        if (plan.Execution.HostConfiguration.Keys.FirstOrDefault(key => key.StartsWith(integration, StringComparison.OrdinalIgnoreCase)) is { } leaked)
        { throw new ArgumentException($"Host configuration reaches the AppHost's command line; set '{leaked}' with WithIntegration so it travels in the private file."); }
        var (options, secrets) = Split(plan.Configuration);
        var identity = "test-" + Guid.NewGuid().ToString("N");
        // Arguments reach the AppHost builder before its program composes modules; adding to
        // builder.Configuration afterwards would miss hosting-layer binds.
        string[] args =
        [
            $"{DigitalBrainHostingOptions.PersistentStorageKey}=false",
            $"{DigitalBrainHostingOptions.ClusterIdKey}={identity}",
            .. options.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key}={pair.Value}"),
            .. plan.Execution.HostConfiguration.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key}={pair.Value}"),
        ];
        var execution = plan.Execution with
        {
            // Integration values travel in the private file, never on a command line.
            PrivateConfiguration = secrets,
            ArtifactDirectory = plan.Execution.ArtifactDirectory ?? Path.Combine(AppContext.BaseDirectory, "e2e-artifacts", identity),
        };
        var session = await AspireTestSession.StartAsync<TAppHost>(args, execution, cancellationToken).ConfigureAwait(false);
        return new AspireBrain(session);
    }

    private static (Dictionary<string, string?> Options, Dictionary<string, string?> Secrets) Split(IReadOnlyDictionary<string, string?> configuration)
    {
        var prefix = DigitalBrainConfiguration.ModulesSectionName + ":";
        Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> secrets = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in configuration)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { options[key] = value; }
            else { secrets[key] = value; }
        }
        return (options, secrets);
    }
}
