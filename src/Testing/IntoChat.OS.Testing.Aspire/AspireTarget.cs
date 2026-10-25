using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Sdk.Platform.Integrations;

namespace IntoChat.OS.Testing.Aspire;

// The consumer's AppHost behind ITestBrain: a fresh cluster on non-persistent storage per brain.
// The browser location is fixed at boot: a container browser is a resource of the AppHost.
public sealed class AspireTarget<TAppHost>(BrowserLocation? browser = null) : ITestTarget where TAppHost : class
{
    public async Task<ITestBrain> StartAsync(TestBrainPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Modules.Count > 0) { throw new NotSupportedException("The AppHost owns composition; use WithOptions/WithIntegration on an Aspire target."); }
        if (plan.Services is not null) { throw new NotSupportedException("ConfigureServices is in-process only; put a fake behind WithIntegration."); }
        if (plan.LicenseCheck is not null) { throw new NotSupportedException("WithLicenseCheck is supported only by the in-process target"); }
        if (plan.Client is not null) { throw new NotSupportedException("An Aspire brain's client belongs to the AppHost."); }
        var integration = IntegrationSchema.SectionName + ":";
        if (plan.Execution.HostConfiguration.Keys.FirstOrDefault(key => key.StartsWith(integration, StringComparison.OrdinalIgnoreCase)) is { } leaked)
        { throw new ArgumentException($"Host configuration reaches the AppHost's command line; set '{leaked}' with WithIntegration so it travels in the private file."); }
        var (options, secrets) = Split(plan.Configuration);
        var identity = "test-" + Guid.NewGuid().ToString("N");
        // A temporary Home per brain: the AppHost projects it to its silos, so state never lands in ~/.intochat.
        var home = TestHome.Create();
        // Arguments reach the AppHost builder before its program composes modules; adding to
        // builder.Configuration afterwards would miss hosting-layer binds.
        string[] args =
        [
            $"{IntoChat.OS.Contracts.Configuration.IntoChatOSNames.HomeKey}={home.Path}",
            // A test brain's Azurite is a throwaway container, like its Home.
            $"{IntoChatOSConfiguration.ModuleOptionsSection("orleans")}:Azurite:PersistentStorage=false",
            $"{IntoChatOSHostingOptions.ClusterIdKey}={identity}",
            .. options.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key}={pair.Value}"),
            .. plan.Execution.HostConfiguration.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key}={pair.Value}"),
        ];
        var execution = plan.Execution with
        {
            // Integration values travel in the private file, never on a command line.
            PrivateConfiguration = secrets,
            ArtifactDirectory = plan.Execution.ArtifactDirectory ?? Path.Combine(AppContext.BaseDirectory, "e2e-artifacts", identity),
        };
        AspireTestSession session;
        try { session = await AspireTestSession.StartAsync<TAppHost>(args, execution, cancellationToken, browser ?? BrowserEnvironment.Location).ConfigureAwait(false); }
        catch
        {
            home.Dispose();
            throw;
        }
        // The Home is deleted after the session stopped the processes writing to it.
        return new AspireBrain(session, home);
    }

    private static (Dictionary<string, string?> Options, Dictionary<string, string?> Secrets) Split(IReadOnlyDictionary<string, string?> configuration)
    {
        var prefix = IntoChatOSConfiguration.ModulesSectionName + ":";
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
