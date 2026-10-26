using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.Aspire;

public sealed class AspireModule() : Module<AspireOptions>(new ModuleId("aspire"))
{
    internal const string ManifestName = "aspire";

    // R4-04: what the module needs from its host (AspireNeurons). The deployment is still a grain (it rolls the cluster from its
    // activation); its registrations stay in Configure until the Framework leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest => AspireNeurons.Manifest;

    // The candidates and their queue are plain neurons (06-06d) on the runtime's host grains.
    public override Type? Neurons => typeof(AspireNeurons);

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Microsoft.Aspire.IDeployment).Assembly;

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
        Neurons = [typeof(ICandidate), typeof(IDeployment)],
        Signals = [typeof(CandidatePromoted), typeof(CandidateFailed), typeof(DeploymentChanged)],
    };

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<Candidates.IProcessRunner, Candidates.SystemProcessRunner>();
        builder.Services.TryAddSingleton<Candidates.ICandidateHost, Candidates.DockerCandidateHost>();
        builder.Services.TryAddSingleton<Deployment.IClusterHost, Deployment.AspireCliClusterHost>();
        builder.Services.TryAddSingleton<Verification.ICandidateVerifier, Verification.CandidateVerifier>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<Deployment.DeploymentNeuron>, Deployment.DeploymentReconcile>());
    }
}

// The DigitalBrain module of Aspire (06-06d): its assembly's [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module(AspireModule.ManifestName)]
public sealed class AspireNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<AspireOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(AspireModule.ManifestName))],
        Secrets: []);
}
