using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Aspire.Hosting;

public interface IIntoChatOSModuleHosting
{
    ModuleId Module { get; }
    void Configure(IntoChatOSBuilder brain);
}
