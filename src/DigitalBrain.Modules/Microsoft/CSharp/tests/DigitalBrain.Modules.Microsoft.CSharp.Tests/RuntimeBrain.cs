using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// A unit brain whose sandbox sessions land on a fake host. In-process brains have no HTTP listener,
// so the runtime edge address is configured rather than derived. The test's own IPinger stands in for
// an installed module contract.
internal static class RuntimeBrain
{
    public static Task<ModuleBrain> StartAsync(FakeSessionHost host, CancellationToken ct, bool http = false, IBehaviorAssemblies? assemblies = null)
    {
        var builder = ModuleTest.Create().WithModule<CSharpModule>().WithReminders()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddHttpClient<SessionRunner>().ConfigurePrimaryHttpMessageHandler(() => host);
                silo.Services.AddSingleton(assemblies ?? new MemoryBehaviorAssemblies());
                silo.Services.AddSingleton(new ScriptContracts([typeof(IPinger).Assembly, typeof(ICSharpFile).Assembly]));
                silo.Services.PostConfigure<CSharpDeploymentSettings>(options =>
                {
                    options.EdgeUrl = "http://edge.test/";
                    options.SandboxUrl = FakeSessionHost.Url;
                });
            });
        return (http ? builder.WithHttpEdge() : builder).StartAsync(ct);
    }
}
