using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Tools;

// The per-brain registry of tool sources, and the capability source that lets search and Jev see them.
public sealed class ToolsModule() : Module(new ModuleId("tools"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IToolSource).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IRuntimeCapabilitySource, ToolCapabilitySource>());
    }
}
