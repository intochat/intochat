using DigitalBrain.Framework;
using DigitalBrain.Platform.Storage;
using Microsoft.Extensions.Hosting;
using Orleans.Storage;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsStorageFacts
{
    [Fact]
    public void LocalStorageIsRegisteredWithoutAMachineRole()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.AddBrainStorage();
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(IGrainStorage)
            && Equals(service.ServiceKey, DigitalBrainNames.ClusterGrainStorage));
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(ISignalJournal));
    }
}
