using System.Reflection;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Aspire.Hosting;

// The generic argument is the module↔ring link: a compiler-checked project reference the hosting
// ring already has, so adapter and module cannot disagree on the id.
public abstract class ModuleHosting<TModule> : IIntoChatOSModuleHosting where TModule : class, new()
{
    public ModuleId Module { get; } = HostedModuleId.Of(typeof(TModule));

    // By default a module's declared secrets (its static Integration/Integrations) are projected to every silo host.
    // A hosting that adds resources, or already projects its own secrets, overrides Configure and does not call base.
    public virtual void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ProjectSecrets(brain);
    }

    protected virtual void ProjectSecrets(IntoChatOSBuilder brain) => ModuleSecretProjection.Project(brain, typeof(TModule));
}

internal static class HostedModuleId
{
    public static ModuleId Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var id = type.GetCustomAttribute<DigitalBrain.ModuleAttribute>()?.Id
            ?? throw new InvalidOperationException($"{type.FullName} has no [Module(id)].");
        return new ModuleId(id);
    }
}
