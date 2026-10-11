using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Monnet.Aspire.Hosting;

public static class MonnetHostingExtensions
{
    // The deployment's merchant: the parameters become the monnet integration. BaseUrl is a module option.
    public static DigitalBrainModuleBuilder<MonnetModuleHosting> WithMonnet(this DigitalBrainModuleBuilder<MonnetModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.GetOrAddProjection(brain => new MonnetHostingState(brain, module.Resource));
        return module;
    }

    private sealed class MonnetHostingState(DigitalBrainBuilder brain, IResourceBuilder<DigitalBrainModuleResource> module) : DigitalBrainModuleProjection
    {
        private IResourceBuilder<ParameterResource>? _apiKey;
        private IResourceBuilder<ParameterResource>? _merchantId;
        private IResourceBuilder<ParameterResource>? _webhookSecret;

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var app = brain.ApplicationBuilder;
            _apiKey ??= app.AddParameter(brain.ResourceName("monnet-api-key"), secret: true)
                .WithDescription(
                    "API key of your Monnet merchant (sandbox credentials from Monnet while the integration is being certified). "
                    + "Sent as the `X-Api-Key` header; only the host receives it.",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            _merchantId ??= app.AddParameter(brain.ResourceName("monnet-merchant-id"))
                .WithDescription("Merchant id Monnet assigned to your account (sent as `X-Merchant-Id`).", enableMarkdown: true)
                .WithParentRelationship(module);
            _webhookSecret ??= app.AddParameter(brain.ResourceName("monnet-webhook-secret"), secret: true)
                .WithDescription(
                    "Secret Monnet signs payment notifications with (HMAC-SHA256 of the body, hex, in `X-Monnet-Signature`). "
                    + "Register `<your deployment origin>/monnet/webhook` as the notification URL at Monnet. "
                    + "Without this secret every notification is refused.",
                    enableMarkdown: true)
                .WithParentRelationship(module);

            builder
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("monnet", "ApiKey")), _apiKey)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("monnet", "MerchantId")), _merchantId)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("monnet", "WebhookSecret")), _webhookSecret);
        }
    }
}
