using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Sdk.Modules;

// What a module's OnConfigure sees of the host: services, configuration and its own bound options. It
// is the seam that keeps module code independent of the silo builder type.
public interface IModuleServices
{
    IServiceCollection Services { get; }
    IConfiguration Configuration { get; }

    // The module's options, bound from IntoChatOS:Modules:{id}:Options and validated.
    T Options<T>() where T : class, IModuleOptions, new();
}
