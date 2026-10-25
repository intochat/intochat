using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Kernel.Composition;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsStorageFacts
{
    // The kernel storage hands the runtime BrainLog to the host-bound cluster seam, and the brain keys to the os custody.
    [Fact]
    public void KernelStorageRegistersTheRuntimeBrainLogAsTheClusterStore()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.UseKernelStorage([]);
        Assert.True(ClusterStoreHosting.HasClusterStore(builder.Services));
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(IBrainKeyCustody));
    }
}
