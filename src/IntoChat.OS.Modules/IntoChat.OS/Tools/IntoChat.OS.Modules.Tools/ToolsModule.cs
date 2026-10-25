using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Tools;

// The per-brain registry of tool sources, and the capability source that lets search and Jev see them.
public sealed class ToolsModule() : Module(new ModuleId("tools"))
{
    // R4-04: what the module needs from its host. It has no neurons; its registrations stay in Configure until the Framework
    // leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);

    public override System.Reflection.Assembly? Contracts => typeof(IToolSource).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IRuntimeCapabilitySource, ToolCapabilitySource>());
    }
}
