using DigitalBrain.Framework;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Trust.Tests.Unit;

// The trust host as it runs: its own silo, the trust grains, platform storage, and no module, kernel or neuron.
// Without the orleans integration it keeps its state in memory on localhost, as a developer's trust host does.
public sealed class TrustHostFacts
{
    [Fact(Timeout = 60_000)]
    public async Task TheTrustHostStartsWithNoModules()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.SiloPort)}"] = Free().ToString(System.Globalization.CultureInfo.InvariantCulture),
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.IdentityPath)}"] = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}", "identity.key"),
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.AdvertisedAddress)}"] = "127.0.0.1",
        });
        builder.UseOrleans(silo => Program.Configure(silo, builder.Configuration));
        using var host = builder.Build();
        await host.StartAsync(ct);

        Assert.Null(await host.Services.GetRequiredService<IGrainFactory>().GetGrain<ISequencerGrain>("p").Get());
        // No module, kernel or brain service is composed (the grain classes themselves are TrustCompositionFacts').
        Assert.Null(host.Services.GetService<DigitalBrain.Framework.Modules.ModuleInventory>());
        Assert.Null(host.Services.GetService<IDigitalBrain>());
        Assert.DoesNotContain(host.Services.GetServices<IStartupTask>(), task => task.GetType().Assembly != typeof(Platform.TrustHosting).Assembly);
        // R66: it advertises and listens on loopback.
        Assert.Equal(System.Net.IPAddress.Loopback, host.Services.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<Orleans.Configuration.EndpointOptions>>().Value.AdvertisedIPAddress);
        await host.StopAsync(ct);
    }

    private static int Free()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
