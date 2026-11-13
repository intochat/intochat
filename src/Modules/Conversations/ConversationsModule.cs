using DigitalBrain.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Conversations;

[Module(ModuleName)]
public sealed class ConversationsModule : Module
{
    internal const string ModuleName = "conversations";

    public static System.Reflection.Assembly? Contracts => typeof(ConversationsModule).Assembly;

    public static ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IConversationDomainService, ConversationDomainService>();
    }
}
