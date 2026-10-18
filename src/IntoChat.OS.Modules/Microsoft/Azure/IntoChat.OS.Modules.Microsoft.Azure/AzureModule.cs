using System.Reflection;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Microsoft.Azure;

// An Azure subscription as a connection: it ensures the resources the OS and its apps run on.
public sealed class AzureModule() : Module<AzureOptions>(new ModuleId("azure"))
{
    public static ConnectorDefinition Connector { get; } = new("azure", "Azure", ["identity"], [])
    {
        Neurons = [typeof(IAzure)],
    };

    public override Assembly? Contracts => typeof(IAzure).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<IAzureArm, ArmAzure>();
    }
}
