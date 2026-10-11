using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Framework.Tests.Unit.Kernel;

public sealed class InstanceRegistrationFacts
{
    [Fact]
    public async Task DecoratingTwiceWrapsTheClusterStorageOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestBrain.Create()
            .ConfigureSilo(silo => { silo.AddKernelRegistration(); silo.AddKernelRegistration(); })
            .StartAsync(ct);

        var storage = Assert.IsType<InstanceRegistration>(host.Services.GetRequiredKeyedService<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage));

        Assert.IsNotType<InstanceRegistration>(storage.Inner);
    }
}
