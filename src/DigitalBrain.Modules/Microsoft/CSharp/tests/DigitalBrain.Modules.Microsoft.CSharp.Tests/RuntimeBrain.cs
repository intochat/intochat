using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// A unit brain whose sandbox sessions land on a fake host. In-process brains have no HTTP listener,
// so the runtime edge address is configured rather than derived. The test's own IPinger stands in for
// an installed module contract.
internal static class RuntimeBrain
{
    // A program that connects and idles: compiling and running, doing nothing.
    public const string Idle = "await using var brain = await DigitalBrainClient.ConnectAsync(args);\nawait Task.Delay(Timeout.Infinite, brain.Stopping);\n";

    public static Dictionary<string, string> One(string source) => new(StringComparer.Ordinal) { [""] = source };

    public static Task<ModuleBrain> StartAsync(FakeSessionHost host, CancellationToken ct, bool http = false, IBehaviorAssemblies? assemblies = null, TimeProvider? time = null, bool traced = false, bool partLog = false)
    {
        var builder = ModuleTest.Create().WithModule<CSharpModule>().WithReminders()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddHttpClient<SessionRunner>().ConfigurePrimaryHttpMessageHandler(() => host);
                silo.Services.AddSingleton(assemblies ?? new MemoryBehaviorAssemblies());
                if (time is not null) { silo.Services.AddSingleton(time); }
                silo.Services.AddSingleton(new DigitalBrain.Framework.CompiledTypes([typeof(IPinger).Assembly, typeof(ICSharpFile).Assembly]));
                silo.Services.PostConfigure<CSharpDeploymentSettings>(options =>
                {
                    options.EdgeUrl = "http://edge.test/";
                    options.SandboxUrl = FakeSessionHost.Url;
                });
            });
        // The product's server hosting propagates traces across grain calls; a fact about traces opts in.
        if (traced) { builder = builder.ConfigureSilo(silo => silo.AddActivityPropagation()).ConfigureClient(client => client.AddActivityPropagation()); }
        if (partLog) { builder = builder.WithPartLog(); }
        return (http ? builder.WithHttpEdge() : builder).StartAsync(ct);
    }

    // The Apps module composes with CSharp, so a fact can drive a real app and read the runtime it owns.
    public static Task<ModuleBrain> StartWithAppsAsync(FakeSessionHost host, CancellationToken ct)
    {
        var builder = ModuleTest.Create().WithModule<AppsModule>().WithModule<CSharpModule>().WithReminders()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddHttpClient<SessionRunner>().ConfigurePrimaryHttpMessageHandler(() => host);
                silo.Services.AddSingleton(new MemoryBehaviorAssemblies());
                silo.Services.AddSingleton(new DigitalBrain.Framework.CompiledTypes([typeof(IPinger).Assembly, typeof(ICSharpFile).Assembly]));
                silo.Services.PostConfigure<CSharpDeploymentSettings>(options =>
                {
                    options.EdgeUrl = "http://edge.test/";
                    options.SandboxUrl = FakeSessionHost.Url;
                });
            });
        return builder.StartAsync(ct);
    }
}
