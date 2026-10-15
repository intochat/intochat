using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Twilio.Aspire.Hosting;

public static class TwilioHostingExtensions
{
    // The deployment's own Twilio account: only the host receives it, as the `twilio` integration.
    public static DigitalBrainModuleBuilder<TwilioModuleHosting> WithTwilio(this DigitalBrainModuleBuilder<TwilioModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.GetOrAddProjection(brain => new TwilioHostingState(brain, module.Resource));
        return module;
    }

    private sealed class TwilioHostingState(
        DigitalBrainBuilder brain,
        IResourceBuilder<DigitalBrainModuleResource> module) : DigitalBrainModuleProjection
    {
        private IResourceBuilder<ParameterResource>? _accountSid;
        private IResourceBuilder<ParameterResource>? _authToken;
        private IResourceBuilder<ParameterResource>? _fromNumber;

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            _accountSid ??= brain.ApplicationBuilder.AddOptionalParameter(brain.ResourceName("twilio-account-sid"))
                .WithDescription(
                    "Account SID (starts with `AC`) from the [Twilio console](https://console.twilio.com/) home page, under Account Info. "
                    + "Set the messaging status webhook to `<your deployment origin>/twilio/status/{brainId}`; this host also passes it on every send.",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            _authToken ??= brain.ApplicationBuilder.AddOptionalParameter(brain.ResourceName("twilio-auth-token"), secret: true)
                .WithDescription(
                    "Auth Token from the same [Twilio console](https://console.twilio.com/) Account Info panel. "
                    + "It authenticates sends and verifies the `X-Twilio-Signature` of status callbacks; only the host receives it.",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            _fromNumber ??= brain.ApplicationBuilder.AddOptionalParameter(brain.ResourceName("twilio-from-number"))
                .WithDescription(
                    "The sending number in E.164 (`+15005550006`), from Phone Numbers > Active numbers. "
                    + "A trial account's number works, but only to verified recipients.",
                    enableMarkdown: true)
                .WithParentRelationship(module);

            builder
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("twilio", "AccountSid")), _accountSid)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("twilio", "AuthToken")), _authToken)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("twilio", "FromNumber")), _fromNumber);
        }
    }
}
