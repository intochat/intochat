using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Revisions;

public sealed class RevisionsModule() : Module<RevisionsOptions>(new ModuleId("revisions"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IRevision).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.AddHttpClient();
        silo.Services.TryAddSingleton<ILauncherClient, HttpLauncherClient>();
        silo.Services.TryAddSingleton<IRevisionVerifier, RevisionVerifier>();
    }
}