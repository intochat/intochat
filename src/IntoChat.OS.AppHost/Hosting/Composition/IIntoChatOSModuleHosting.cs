using DigitalBrain.Modules.Options;
using IntoChat.OS;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

public interface IIntoChatOSModuleHosting
{
    ModuleId Module { get; }
    void Configure(IntoChatOSBuilder brain);
}
