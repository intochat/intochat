global using DigitalBrain.Brains;
global using DigitalBrain.Neurons;
global using DigitalBrain.Signals;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Authoring;

// Unit authoring: the Creator's Author and Builder, the build flow (flow.build), the synapse studio (ino.synapses) and the deploy
// gate that runs a unit's scenarios on the simulation runtime. Every contract it implements stays in the catalog's contracts
// assembly, which the `apps` module declares, so this module declares none of its own; its types keep the namespaces of those
// contracts (IntoChat.OS.Modules.Apps and .Flows). The Author and Builder models are the `apps` module's options (AppsOptions).
[DigitalBrain.Modules.Module(ModuleName)]
public sealed class AuthoringModule : DigitalBrain.Modules.Module
{
    internal const string ModuleName = "authoring";

    public static System.Reflection.Assembly? Contracts => null;

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<IDrafter>(sp => sp.GetRequiredService<Creator>());
        services.TryAddSingleton<ICreator>(sp => sp.GetRequiredService<Creator>());
        services.TryAddSingleton<ICreatorModel, SiloCreatorModel>();
        services.TryAddSingleton<Creator>();
        services.TryAddSingleton<IUnitVerifier, UnitVerifier>();
        services.TryAddSingleton<IPolicyModel, CreatorPolicyModel>();
    }
}
