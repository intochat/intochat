using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Aspire.Hosting;

// The generic argument is the module↔ring link: a compiler-checked project reference the hosting
// ring already has, so adapter and module cannot disagree on the id.
public abstract class ModuleHosting<TModule> : IIntoChatOSModuleHosting where TModule : Module, new()
{
    public ModuleId Module { get; } = new TModule().Id;

    // By default a module's declared secrets (its static Integration/Integrations) are projected to every silo host.
    // A hosting that adds resources, or already projects its own secrets, overrides Configure and does not call base.
    public virtual void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ProjectSecrets(brain);
    }

    protected virtual void ProjectSecrets(IntoChatOSBuilder brain) => ModuleSecretProjection.Project(brain, typeof(TModule));
}
