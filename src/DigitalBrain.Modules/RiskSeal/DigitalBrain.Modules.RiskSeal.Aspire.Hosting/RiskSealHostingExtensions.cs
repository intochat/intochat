using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.RiskSeal.Aspire.Hosting;

public static class RiskSealHostingExtensions
{
    // Only the host receives the API key; it backs every `deployment` RiskSeal connection.
    public static DigitalBrainModuleBuilder<RiskSealModuleHosting> WithRiskSeal(this DigitalBrainModuleBuilder<RiskSealModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.GetOrAddProjection(brain => new RiskSealHostingState(brain, module.Resource));
        return module;
    }

    private sealed class RiskSealHostingState(
        DigitalBrainBuilder brain,
        IResourceBuilder<DigitalBrainModuleResource> module) : DigitalBrainModuleProjection
    {
        private IResourceBuilder<ParameterResource>? _apiKey;

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            _apiKey ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("riskseal-api-key"), secret: true)
                .WithDescription(
                    "API key from your RiskSeal account. It scores email, phone and IP risk for every "
                    + "`riskseal` connection using the `deployment` method; users can still paste their own key (`api-key`).",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("riskseal", "ApiKey")), _apiKey);
        }
    }
}
