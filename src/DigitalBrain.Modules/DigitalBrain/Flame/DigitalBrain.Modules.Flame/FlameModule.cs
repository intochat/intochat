using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Routing;
using Orleans.Hosting;

namespace DigitalBrain.Flame;

// Flame: scenes are edge neurons whose external system is a person; the Flutter shell draws them with Flame.
// Apps write scenes and fire events; the shell reports gestures; the stage is the person's scene stack.
public sealed class FlameModule() : Module(new ModuleId("flame")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(Scene.IScene).Assembly;

    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);

    public void Configure(IEndpointRouteBuilder endpoints) => FlameEndpoints.Map(endpoints);
}
