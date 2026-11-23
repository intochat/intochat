using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.Aspire;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class AspireModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "aspire";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<AspireOptions>(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts => typeof(IDeployment).Assembly;

    // Candidate builds receive model credentials and their own fresh stores.
    public const string WorkdirField = "Workdir";
    public const string CandidateModelField = "CandidateModel";
    // Each role's front (its fixed port) as JSON, {role: url}: a roll reaches an instance the way the front does.
    public const string FrontsField = "Fronts";

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("aspire", "Aspire")
        .RequiresSetting(WorkdirField)
        .OffersSetting(FrontsField)
        .OffersSecret(CandidateModelField);

    // Discovery refuses a connector without an authentication method; the deployment's Workdir is its only credential.
    public static ConnectorDefinition Connector => new("aspire", "Aspire", ["deployment"], ["aspire"])
    {
        Neurons = [typeof(IAspire), typeof(ICandidate), typeof(IDeployment)],
        Signals = [typeof(AspireTelemetryChanged), typeof(AspireResourceRestarted), typeof(AspireResourceRestartRequested), typeof(AspireResourceRestartRefused), typeof(CandidatePromoted), typeof(CandidateFailed), typeof(DeploymentChanged)],
    };

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<Candidates.IProcessRunner, Candidates.SystemProcessRunner>();
        services.TryAddSingleton(provider => new AspireMcp(provider.GetService<global::Microsoft.Extensions.Configuration.IConfiguration>()));
        services.TryAddSingleton<IAspireTools>(provider => provider.GetRequiredService<AspireMcp>());
        services.TryAddSingleton(AspireInstance.FromEnvironment());
        var modeValue = configuration.GetSection(ModuleConfiguration.ModuleOptionsSection(ManifestName))[nameof(AspireOptions.CandidateHostMode)];
        if (Enum.TryParse<CandidateHostMode>(modeValue, ignoreCase: true, out var hostMode) && hostMode == CandidateHostMode.Process)
        {
            services.TryAddSingleton<Candidates.ICandidateHost, Candidates.ProcessCandidateHost>();
        }
        else
        {
            services.TryAddSingleton<Candidates.ICandidateHost, Candidates.DockerCandidateHost>();
        }
        services.TryAddSingleton<Deployment.IClusterHost, Deployment.AspireCliClusterHost>();
        services.TryAddSingleton<Verification.ICandidateVerifier, Verification.CandidateVerifier>();
        // The Aspire app (Screens/AspireApp) ships with this component.
        services.AddSingleton(new CompiledAppsRegistration(typeof(AspireModule).Assembly));
    }
}
