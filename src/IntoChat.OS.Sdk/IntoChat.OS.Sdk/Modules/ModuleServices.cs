using IntoChat.OS.Sdk.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Sdk.Modules;

// Constructed by the generated Module.Configure shell (moves to the Sdk assembly in 09-07).
public sealed class ModuleServices(IServiceCollection services, IConfiguration configuration, ModuleId module) : IModuleServices
{
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));
    public IConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public T Options<T>() where T : class, IModuleOptions, new()
        => Configuration.GetModuleOptions<T>(module.ToString());
}
