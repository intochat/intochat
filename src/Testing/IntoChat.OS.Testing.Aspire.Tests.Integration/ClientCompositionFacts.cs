using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using IntoChat.OS.Testing.Neurons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DigitalBrain.Spi;

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
        var brains = host.Services.GetRequiredService<IIntoChatOSBrains>();

        switch (service)
        {
            case "get":
                Assert.IsAssignableFrom<IPulse>(brains.Get<IPulse>("owner/pulse"));
                break;
            case "kernel":
                Assert.NotNull(host.Services.GetRequiredService<IBrainObserver>());
                break;
            case "signals":
                var pulse = brains.Get<IPulse>("owner/pulse");
                Assert.NotNull(brains.Open("owner").On<Pulsed>(pulse, () => Task.CompletedTask, TestContext.Current.CancellationToken));
                break;
        }
    }
}
