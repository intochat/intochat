using DigitalBrain.Modules.Options;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI;

// IOptions<AIOptions> is bound from the module manifest settings; this is the one direct read for
// construction-time consumers. Ollama model markers bind natively under Options:Ollama:Models.
internal static class AIOptionsRuntime
{
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void InitializeConfigurationRoot() => DigitalBrain.Modules.Configuration.ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };
    internal static AIOptions Read(IConfiguration configuration)
        => configuration.GetModuleOptions<AIOptions>("ai");
}
