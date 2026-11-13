using DigitalBrain.Modules;

namespace IntoChat.OS.Modules.Authoring;

[Module(ModuleName)]
public sealed class AuthoringModule : Module
{
    internal const string ModuleName = "authoring";

    public static System.Reflection.Assembly? Contracts => typeof(AuthoringModule).Assembly;

    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}
