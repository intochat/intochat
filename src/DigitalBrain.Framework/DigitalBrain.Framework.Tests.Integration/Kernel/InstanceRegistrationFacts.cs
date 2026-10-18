using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Framework.Tests.Integration.Kernel;

public sealed class InstanceRegistrationFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task DecoratingTwiceWrapsTheClusterStorageOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestBrain.Create()
            .ConfigureServices(services => { InstanceRegistration.TryDecorate(services); InstanceRegistration.TryDecorate(services); })
            .StartAsync(ct);

        var storage = Assert.IsType<InstanceRegistration>(host.Services.GetRequiredKeyedService<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage));

        Assert.IsNotType<InstanceRegistration>(storage.Inner);
    }
}
