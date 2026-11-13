using DigitalBrain.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Authoring;

[Module(ModuleName)]
public sealed class AuthoringModule : Module
{
    internal const string ModuleName = "authoring";

    public static System.Reflection.Assembly? Contracts => typeof(AuthoringModule).Assembly;

    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<AuthorNeuron>();
        services.TryAddSingleton<IAuthorNeuron>(sp => sp.GetRequiredService<AuthorNeuron>());
    }
}
