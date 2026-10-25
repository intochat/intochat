using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework.Tests.Integration.Kernel;

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

        var storage = Assert.IsType<InstanceRegistration>(host.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.LegacyClusterGrainStorage));

        Assert.IsNotType<InstanceRegistration>(storage.Inner);
    }
}
