using DigitalBrain;
using DigitalBrain.Kernel;
using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireModuleHosting : ModuleHosting<DigitalBrain.Microsoft.Aspire.AspireModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var bridgeKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        new DigitalBrainModuleBuilder<DigitalBrain.Microsoft.Aspire.AspireModuleHosting>(brain).AddProjection(new BridgeKeyProjection(bridgeKey));
        brain.ApplicationBuilder.Services.AddHostedService(services => ActivatorUtilities.CreateInstance<AspireBridgeClient>(services, bridgeKey));
    }

    private sealed class BridgeKeyProjection(string bridgeKey) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(AspireOptions.ConfigurationRoot, "BridgeKey"), bridgeKey);
        }
    }
}
