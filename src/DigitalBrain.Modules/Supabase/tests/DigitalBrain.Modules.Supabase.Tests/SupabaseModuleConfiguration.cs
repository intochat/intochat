using DigitalBrain.Supabase;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Modules.Supabase.Tests;

public static class SupabaseModuleConfiguration
{
    public static ModuleConfiguration<SupabaseModule> WithProvider<TProvider>(this ModuleConfiguration<SupabaseModule> module)
        where TProvider : class, ISupabaseProvider
    {
        module.ConfigureLocalServices(services => services.Replace(ServiceDescriptor.Singleton<ISupabaseProvider, TProvider>()));
        return module;
    }
}
