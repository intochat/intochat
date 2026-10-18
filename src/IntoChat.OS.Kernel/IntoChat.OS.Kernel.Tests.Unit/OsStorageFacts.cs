using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Storage;
using Microsoft.Extensions.Hosting;
using Orleans.Storage;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsStorageFacts
{
    [Fact]
    public void LocalStorageIsRegisteredWithoutAMachineRole()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.AddBrainStorage();
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(IGrainStorage)
            && Equals(service.ServiceKey, IntoChatOSNames.ClusterGrainStorage));
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(ISignalJournal));
    }
}
