using IntoChat.OS.Framework.Configuration;
using Microsoft.Extensions.Configuration;
namespace IntoChat.OS.AI;

// IOptions<AIOptions> is bound by Module<AIOptions>.AddOptions; this is the one direct read for
// construction-time consumers. Ollama model markers bind natively under Options:Ollama:Models.
internal static class AIOptionsRuntime
{
    internal static AIOptions Read(IConfiguration configuration)
        => configuration.GetModuleOptions<AIOptions>("ai");
}
