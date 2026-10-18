using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Routing;
using Orleans.Hosting;

namespace IntoChat.OS.Flame;

// Flame: scenes are edge neurons whose external system is a person; the Flutter shell draws them with Flame.
// Apps write scenes and fire events; the shell reports gestures.
public sealed class FlameModule() : Module(new ModuleId("flame")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(Scene.IScene).Assembly;

    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);

    public void Configure(IEndpointRouteBuilder endpoints) => FlameEndpoints.Map(endpoints);
}
