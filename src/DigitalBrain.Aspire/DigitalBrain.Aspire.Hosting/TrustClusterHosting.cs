using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Aspire.Hosting;

// The trust cluster beside the OS machines (Task 18): its own host on the orleans integration's tables and blobs,
// reached by each machine through a private gateway client.
public static class TrustClusterHosting
{
    public const string GatewayPortKey = "DigitalBrain:Platform:Trust:GatewayPort";
    public const string AdvertisedAddressKey = "DigitalBrain:Platform:Trust:AdvertisedAddress";
    public const string GatewayKey = "DigitalBrain:Platform:Trust:Gateway";
    public const string Loopback = "127.0.0.1";

    // R66: on one PC the trust silo advertises and listens on loopback, so a machine's localhost gateway reaches it.
    public static IResourceBuilder<T> AsTrustCluster<T>(this IResourceBuilder<T> trust, DigitalBrainBuilder brain, int gatewayPort)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(brain);
        trust.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(GatewayPortKey), gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(AdvertisedAddressKey), Loopback);
        if (brain.Clustering is { } tables && brain.GrainState is { } blobs)
        {
            trust.WithEnvironment(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "Clustering"), tables.Resource.ConnectionStringExpression)
                .WithEnvironment(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "GrainState"), blobs.Resource.ConnectionStringExpression)
                .WaitFor(tables).WaitFor(blobs);
        }
        return trust;
    }

    public static string GatewayOf(int gatewayPort) => $"localhost:{gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
