using System.Reflection;
using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.Windows;

// A Windows PC as an edge: the machine, its files and its console, served only by a silo on that PC.
public sealed class WindowsModule() : Module<WindowsOptions>(new ModuleId("windows"))
{
    public override Assembly? Contracts => typeof(WindowsOptions).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<IMachineIdentity, MachineIdentity>();
    }
}
