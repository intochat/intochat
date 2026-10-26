using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp;
using IntoChat.OS.Modules.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A brain whose sandbox sessions land on a fake host; the test's own IPinger stands in for an installed
// module contract. Sessions dial the brain's own HTTP edge, as in development.
internal static class RuntimeBrain
{
    // A program that connects and idles: compiling and running, doing nothing.
    public const string Idle = "await using var brain = await IntoChatOSClient.ConnectAsync(args);\nawait Task.Delay(Timeout.Infinite, brain.Stopping);\n";

    public static Dictionary<string, string> One(string source) => new(StringComparer.Ordinal) { [""] = source };

    public static TestBrain Create(HttpMessageHandler host, bool apps = false)
    {
        var builder = apps ? TestBrain.Create().WithModule<AppsModule>() : TestBrain.Create();
        return builder.WithModule<CSharpModule>().WithModule<PingerModule>()
            // The sandbox address is a deployment setting, not an integration: the fake host takes the
            // session runner's transport and the address it is told to dial.
            .ConfigureServices(services =>
            {
                services.AddHttpClient<SessionRunner>().ConfigurePrimaryHttpMessageHandler(() => host);
                services.PostConfigure<CSharpOptions>(options => options.SandboxUrl = FakeSessionHost.Url);
            });
    }

    public static Task<ITestBrain> StartAsync(FakeSessionHost host, CancellationToken ct, IBehaviorAssemblies? assemblies = null, TimeProvider? time = null, ISignalJournal? journal = null, bool apps = false)
    {
        var builder = Create(host, apps);
        // The clock moves sessions past their heartbeat limit; the assembly store and the journal are storage seams.
        if (time is not null) { builder = builder.ConfigureServices(services => services.AddSingleton(time)); }
        if (assemblies is not null) { builder = builder.ConfigureServices(services => services.AddSingleton(assemblies)); }
        if (journal is not null) { builder = builder.ConfigureServices(services => services.AddSingleton(journal)); }
        return builder.StartAsync(ct);
    }

    public static Task<ITestBrain> StartWithAppsAsync(FakeSessionHost host, CancellationToken ct) => Create(host, apps: true).StartAsync(ct);

    public static IBrainPrograms Programs(ITestBrain brain, string scope)
        => brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainPrograms>(BrainRuntimeKeys.For(scope));

    // How a plain neuron (06-08: an app) is named to the runtime edge, and the alias of an app request.
    public static string Plain<T>(ITestBrain brain, string key) where T : class
        => IntoChat.OS.Modules.Microsoft.CSharp.Runtime.PlainSources.Of(
            global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>(brain.Services).IdOf(typeof(T), key));

    public static readonly string Requested = IntoChat.OS.Runtime.PlainSignalTypes.AliasOf(typeof(IntoChat.OS.Modules.Apps.Signals.AppRequested));

    public static IBrainRuntime Runtime(ITestBrain brain, string scope) => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(scope));

    public static IBrainRuntimeEdge Edge(ITestBrain brain, string scope)
        => brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For(scope));

    public static string Fingerprint(ITestBrain brain)
        => IntoChat.OS.Client.Runtime.ContractsFingerprint.Of(brain.Services.GetRequiredService<IntoChat.OS.Framework.CompiledTypes>().Assemblies);
}
