using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting;

public static class RuntimeHosting
{
    public const string OrleansSiloEndpoint = "orleans-silo";

    // What the Orleans resource reference adds to a silo and an OS kernel does not keep (IntoChatOSHostingExtensions).
    public const string OrleansGatewayEndpoint = "orleans-gateway";
    public const string OrleansGatewayPortVariable = "Orleans__Endpoints__GatewayPort";

    // The silo port only: no OS host is an Orleans cluster client, so a kernel opens no gateway (the kernel's profile leaves the
    // gateway closed when no gateway port is configured).
    public static IResourceBuilder<T> WithAllocatedOrleansPorts<T>(this IResourceBuilder<T> machine)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(machine);
        machine.WithEndpoint(name: OrleansSiloEndpoint, scheme: "tcp", isProxied: false);
        return machine
            .WithEnvironment("Orleans__Endpoints__SiloPort", machine.GetEndpoint(OrleansSiloEndpoint).Property(EndpointProperty.Port));
    }

    public const int ContainerSiloPort = 11111;

    // DCP requires a container endpoint to name its container-side port; the kernel inside listens on that fixed port.
    public static IResourceBuilder<ContainerResource> WithAllocatedOrleansPorts(this IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container
            .WithEndpoint(name: OrleansSiloEndpoint, scheme: "tcp", targetPort: ContainerSiloPort, isProxied: false)
            .WithEnvironment("Orleans__Endpoints__SiloPort", ContainerSiloPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
