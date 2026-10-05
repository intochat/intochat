using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Specs;

public sealed class SpecsModule() : Module(new ModuleId("specs"))
{
    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton(TimeProvider.System);
    }
}
