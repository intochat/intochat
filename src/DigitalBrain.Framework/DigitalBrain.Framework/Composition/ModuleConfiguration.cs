using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Framework;

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
