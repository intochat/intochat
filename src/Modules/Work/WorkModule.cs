using DigitalBrain.Modules;

namespace IntoChat.OS.Modules.Work;

[Module(ModuleName)]
public sealed class WorkModule : Module
{
    internal const string ModuleName = "work";

    public static System.Reflection.Assembly? Contracts => typeof(WorkModule).Assembly;

    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}
