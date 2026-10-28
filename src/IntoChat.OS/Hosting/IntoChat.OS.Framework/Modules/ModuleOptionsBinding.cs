using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Modules;

// The transitional runtime binds a module's options itself, from public Sdk members only (D-05):
// a Module<TOptions> gets IOptions<TOptions> from IntoChatOS:Modules:{id}:Options, validated at host start.
// A plain Module registers nothing. Phase 3 replaces this with the manifest Settings.
public static class ModuleOptionsBinding
{
    public static void Add(IServiceCollection services, Module module)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(module);
        for (var type = module.GetType(); type is not null; type = type.BaseType)
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Module<>)) { continue; }
            typeof(ModuleOptionsBinding).GetMethod(nameof(Bind), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [services, module.Id]);
            return;
        }
    }

    private static void Bind<TOptions>(IServiceCollection services, ModuleId id) where TOptions : class, IModuleOptions, new()
        => services.AddOptions<TOptions>()
            .BindConfiguration(IntoChatOSConfiguration.ModuleOptionsSection(id))
            .Validate(options =>
            {
                ModuleOptionsSerialization.Validate(options, id.ToString());
                return true;
            }).ValidateOnStart();
}
