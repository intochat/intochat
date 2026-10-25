using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Routing;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Flame;

// Flame: scenes are edge neurons whose external system is a person; the Flutter shell draws them with Flame.
// Apps write scenes and fire events; the shell reports gestures.
public sealed class FlameModule() : Module(new ModuleId(ModuleName)), IHttpModule
{
    internal const string ModuleName = "flame";

    // R4-04: what the module needs from its host (FlameNeurons).
    public static DigitalBrain.ModuleManifest Manifest => FlameNeurons.Manifest;

    // The scene neuron is a plain neuron (06-06a) on the runtime's host grains.
    public override Type? Neurons => typeof(FlameNeurons);

    public override System.Reflection.Assembly? Contracts => typeof(Scene.IScene).Assembly;

    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);

    public void Configure(IEndpointRouteBuilder endpoints) => FlameEndpoints.Map(endpoints);
}

// The DigitalBrain module of Flame (06-06a): its assembly's [Neuron] classes (SceneNeuron) run on the runtime's host grains, on
// TestOS and on the silo alike.
[DigitalBrain.Module(FlameModule.ModuleName)]
public sealed class FlameNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
