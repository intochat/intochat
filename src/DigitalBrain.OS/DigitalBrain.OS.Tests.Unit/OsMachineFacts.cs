using DigitalBrain.Framework;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Storage;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsMachineFacts
{
    private static bool HasPartLog(params KeyValuePair<string, string?>[] settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddMachinePartLogWhenConfigured();
        return builder.Services.Any(service => service.ServiceType == typeof(IGrainStorage)
            && Equals(service.ServiceKey, DigitalBrainNames.ClusterGrainStorage));
    }

    [Fact]
    public void ThePartLogIsOnlyRegisteredWhenAMachineRoleIsConfigured()
    {
        Assert.False(HasPartLog());
        Assert.True(HasPartLog(new KeyValuePair<string, string?>($"{DigitalBrain.Platform.Parts.PartsOptions.Key}:{nameof(DigitalBrain.Platform.Parts.PartsOptions.Role)}", "Own")));
    }
}
