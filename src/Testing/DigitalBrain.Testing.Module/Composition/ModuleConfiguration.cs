using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using ModuleContract = DigitalBrain.Framework.Module;

namespace DigitalBrain.Testing.Module;

public sealed class ModuleConfiguration<TModule> where TModule : ModuleContract, new()
{
    private readonly ModuleDraft _draft;
    internal ModuleConfiguration(ModuleDraft draft) => _draft = draft;

    public void ConfigureLocalServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _draft.LocalServices.Add(configure);
    }
}
