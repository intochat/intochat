using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireModule() : Module<AspireOptions>(new ModuleId("aspire"))
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Microsoft.Aspire.IDeployment).Assembly;

    // Workdir: where candidates are built. The two secrets are what the hosting ring hands a candidate host and
    // nothing else of the live deployment: CandidateModel, the model Integrations' environment as a JSON object
    // (environment name to value), and LiveGrainState, the live grain-state connection the storage copy reads.
    public const string WorkdirField = "Workdir";
    public const string CandidateModelField = "CandidateModel";
    public const string LiveGrainStateField = "LiveGrainState";
    // Each role's front (its fixed port) as JSON, {role: url}: a roll reaches an instance the way the front does.
    public const string FrontsField = "Fronts";

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("aspire", "Aspire")
        .RequiresSetting(WorkdirField)
        .OffersSetting(FrontsField)
        .OffersSecret(CandidateModelField)
        .OffersSecret(LiveGrainStateField);

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
