using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Flame;

// Flame: scenes are edge neurons whose external system is a person; the Flutter shell draws them with Flame.
// Apps write scenes and fire events; the shell reports gestures. SceneNeuron is a plain neuron on the runtime's host grains.
[DigitalBrain.Module(ModuleName)]
public sealed class FlameModule : DigitalBrain.Module, IHttpModule
{
    internal const string ModuleName = "flame";

    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(Scene.IScene).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints) => FlameEndpoints.Map(endpoints);
}
