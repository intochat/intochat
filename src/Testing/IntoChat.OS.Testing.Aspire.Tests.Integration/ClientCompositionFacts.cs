using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using IntoChat.OS.Testing.Neurons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Testing.Aspire.Tests.Integration;

public sealed class ClientCompositionFacts
{
    [Theory]
    [InlineData("get")]
    [InlineData("kernel")]
    [InlineData("signals")]
    public void TheAspireClientComposesEveryBrainServiceBeforeConnecting(string service)
    {
        using var host = Host.CreateDefaultBuilder().UseOrleansClient(client =>
        {
            client.UseStaticClustering(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 30000));
            AspireTestSession.AddClientServices(client.Services);
        }).Build();
        var brain = host.Services.GetRequiredService<IIntoChatOSBrains>().Open("owner");

        switch (service)
        {
            case "get":
                Assert.IsAssignableFrom<IPulse>(brain.Get<IPulse>(new NeuronId("owner/pulse")));
                break;
            case "kernel":
                Assert.Same(host.Services.GetRequiredService<IKernelView>(), brain.Kernel);
                break;
            case "signals":
                var pulse = brain.Get<IPulse>(new NeuronId("owner/pulse"));
                Assert.NotNull(brain.On<Pulsed>(pulse, () => Task.CompletedTask, TestContext.Current.CancellationToken));
                break;
        }
    }
}
