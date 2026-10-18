using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Aspire.Hosting;

// The generic argument is the module↔ring link: a compiler-checked project reference the hosting
// ring already has, so adapter and module cannot disagree on the id.
public abstract class ModuleHosting<TModule> : IIntoChatOSModuleHosting where TModule : Module, new()
{
    public ModuleId Module { get; } = new TModule().Id;
    public abstract void Configure(IntoChatOSBuilder brain);
}
