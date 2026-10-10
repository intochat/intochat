using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Platform.Parts;

public static class MachineTrustHosting
{
    // What a machine adds for handing parts to other machines of its trust cluster (Task 18): releasing a part
    // deactivates its grains here first. The trust clients and machine registration come with the platform.
    public static ISiloBuilder AddMachineHandover(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<PartActivations>();
        return silo;
    }
}
