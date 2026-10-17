using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace DigitalBrain.Aspire.Hosting;

public static class RuntimeHosting
{
    public const string OrleansSiloEndpoint = "orleans-silo";
    public const string OrleansGatewayEndpoint = "orleans-gateway";

    public static IResourceBuilder<T> WithAllocatedOrleansPorts<T>(this IResourceBuilder<T> machine)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(machine);
        machine.WithEndpoint(name: OrleansSiloEndpoint, scheme: "tcp", isProxied: false)
            .WithEndpoint(name: OrleansGatewayEndpoint, scheme: "tcp", isProxied: false);
        return machine
            .WithEnvironment("Orleans__Endpoints__SiloPort", machine.GetEndpoint(OrleansSiloEndpoint).Property(EndpointProperty.Port))
            .WithEnvironment("Orleans__Endpoints__GatewayPort", machine.GetEndpoint(OrleansGatewayEndpoint).Property(EndpointProperty.Port));
    }

}
