using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Modules;

namespace IntoChat.OS.Aspire.Hosting;

public interface IIntoChatOSModuleHosting
{
    ModuleId Module { get; }
    void Configure(IntoChatOSBuilder brain);
}
