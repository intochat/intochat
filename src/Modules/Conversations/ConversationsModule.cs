using DigitalBrain.Modules;

namespace IntoChat.OS.Modules.Conversations;

[Module(ModuleName)]
public sealed class ConversationsModule : Module
{
    internal const string ModuleName = "conversations";

    public static System.Reflection.Assembly? Contracts => typeof(ConversationsModule).Assembly;

    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}
