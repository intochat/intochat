using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Testing.Aspire;
namespace DigitalBrain.Platform.Tests.E2E;

public sealed class ProjectEndpointFacts
{
    [Fact]
    public void ConcurrentHostSessionsAllocateProjectHttpPortsWithoutChangingContainerEndpoints()
    {
        var silo = new ProjectResource("runtime");
        var client = new ProjectResource("mcp-client");
        var container = new ContainerResource("external", "server");
        var runtimeHttp = new EndpointAnnotation(ProtocolType.Tcp, "http", name: "http", port: 5080, targetPort: 5080);
        var clientHttp = new EndpointAnnotation(ProtocolType.Tcp, "http", name: "http", port: 5081, targetPort: 5081);
        var externalHttp = new EndpointAnnotation(ProtocolType.Tcp, "http", name: "http", port: 9090, targetPort: 80);
        var siloTcp = new EndpointAnnotation(ProtocolType.Tcp, "tcp", name: "orleans-silo", port: 11111, targetPort: 11111);
        silo.Annotations.Add(runtimeHttp);
        silo.Annotations.Add(siloTcp);
        client.Annotations.Add(clientHttp);
        container.Annotations.Add(externalHttp);
        AspireTestSession.AllocateProjectHttpPorts([silo, client, container]);
        Assert.Null(runtimeHttp.Port);
        Assert.Null(runtimeHttp.TargetPort);
        Assert.Null(clientHttp.Port);
        Assert.Null(clientHttp.TargetPort);
        Assert.False(clientHttp.IsProxied);
        Assert.Equal(9090, externalHttp.Port);
        Assert.Equal(80, externalHttp.TargetPort);
        Assert.Equal(11111, siloTcp.Port);
    }
}
