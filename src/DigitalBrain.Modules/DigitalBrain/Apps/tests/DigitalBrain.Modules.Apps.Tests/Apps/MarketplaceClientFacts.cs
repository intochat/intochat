using System.Reflection;
using DigitalBrain.Framework.Platform.Marketplace;

namespace DigitalBrain.Apps.Tests.Apps;

// The Marketplace catalog lives in the trust cluster (R14): the neuron holds no state and reaches it, like its
// write guard, only through the platform's client.
public sealed class MarketplaceClientFacts
{
    [Fact]
    public void TheMarketplaceNeuronAndItsGuardReachTheCatalogOnlyThroughTheClient()
    {
        foreach (var type in new[] { typeof(MarketplaceNeuron), typeof(MarketplaceWriteGuard) })
        {
            var parameters = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters()).ToArray();
            Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(IMarketplaceClient));
            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IGrainFactory) || parameter.ParameterType == typeof(IClusterClient)
                || parameter.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "PersistentStateAttribute"));
        }
    }
}
