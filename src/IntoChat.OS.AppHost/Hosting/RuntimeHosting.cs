using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting;

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

    public const int ContainerSiloPort = 11111;
    public const int ContainerGatewayPort = 30000;

    // DCP requires a container endpoint to name its container-side port; the kernel inside listens on that fixed port.
    public static IResourceBuilder<ContainerResource> WithAllocatedOrleansPorts(this IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container
            .WithEndpoint(name: OrleansSiloEndpoint, scheme: "tcp", targetPort: ContainerSiloPort, isProxied: false)
            .WithEndpoint(name: OrleansGatewayEndpoint, scheme: "tcp", targetPort: ContainerGatewayPort, isProxied: false)
            .WithEnvironment("Orleans__Endpoints__SiloPort", ContainerSiloPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("Orleans__Endpoints__GatewayPort", ContainerGatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
