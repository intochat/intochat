using DigitalBrain;
using DigitalBrain.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Kernel;

public sealed class ModuleConfiguration<TModule> where TModule : Module, new()
{
    private readonly ModuleDraft _draft;
    internal ModuleConfiguration(ModuleDraft draft) => _draft = draft;

    public void ConfigureLocalServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _draft.LocalServices.Add(configure);
    }
}
