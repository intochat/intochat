using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Equifax.Aspire.Hosting;

public static class EquifaxHostingExtensions
{
    // Only the host receives the client credentials; the base URL stays a module option or integration setting.
    public static DigitalBrainModuleBuilder<EquifaxModuleHosting> WithEquifax(this DigitalBrainModuleBuilder<EquifaxModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _ = module.GetOrAddProjection(brain => new EquifaxHostingState(brain, module.Resource));
        return module;
    }

    private sealed class EquifaxHostingState(
        DigitalBrainBuilder brain,
        IResourceBuilder<DigitalBrainModuleResource> module) : DigitalBrainModuleProjection
    {
        private IResourceBuilder<ParameterResource>? _clientId;
        private IResourceBuilder<ParameterResource>? _clientSecret;

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            _clientId ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("equifax-client-id"), secret: true)
                .WithDescription(
                    "Client ID of your Equifax Peru application from the [Equifax developer portal](https://developer.equifax.com/). "
                    + "Use the sandbox credentials issued with the Equifax Peru contract.",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            _clientSecret ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("equifax-client-secret"), secret: true)
                .WithDescription(
                    "Client secret of the same Equifax application (OAuth2 client credentials). Only the host receives it.",
                    enableMarkdown: true)
                .WithParentRelationship(module);

            builder
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("equifax", "ClientId")), _clientId)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("equifax", "ClientSecret")), _clientSecret);
        }
    }
}
