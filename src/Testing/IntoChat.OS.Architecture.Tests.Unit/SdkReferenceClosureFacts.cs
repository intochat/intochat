using System.Reflection;
using IntoChat.OS.Sdk.Modules;
using Module = IntoChat.OS.Sdk.Modules.Module;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A third party compiles against IntoChat.OS.Sdk (and, for the privileged seams, Sdk.Identity) alone. The
// closure of those assemblies, and of the neuron-free sample module built on them, must never reach the
// runtime (Framework), the platform (Authority) or the client.
public sealed class SdkReferenceClosureFacts
{
    private static readonly string[] Forbidden = ["IntoChat.OS.Framework", "IntoChat.OS.Connections", "IntoChat.OS.Client"];

    [Fact]
    public void AuthoringTypesLiveInTheSdkAssembly()
    {
        Assert.Equal("IntoChat.OS.Sdk", typeof(Module).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Sdk", typeof(Module<>).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Sdk", typeof(ModuleServices).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Sdk", typeof(IModuleServices).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Sdk", typeof(IntoChat.OS.Sdk.Validation.ValidationIssue).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Contracts.Compute", typeof(IntoChat.OS.Contracts.Compute.ICostMeter).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Sdk.Identity", typeof(IntoChat.OS.Sdk.Identity.IPrivilegedServices).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Sdk.Identity", typeof(IntoChat.OS.Sdk.Platform.Identity.ISessionIssuer).Assembly.GetName().Name);
    }

    [Fact]
    public void SdkClosureExcludesTheRuntimeAndThePlatform()
    {
        var roots = new[] { "IntoChat.OS.Sdk", "IntoChat.OS.Sdk.Identity", "IntoChat.OS.Sdk.Http", "IntoChat.OS.Sdk.SampleModule" };
        var loaded = roots.Select(Assembly.Load).ToArray();
        // Non-vacuous: the sample module is really in the walk, not silently skipped.
        Assert.Contains("IntoChat.OS.Sdk.SampleModule", loaded.Select(assembly => assembly.GetName().Name));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>(loaded);
        while (pending.Count > 0)
        {
            var assembly = pending.Pop();
            if (!seen.Add(assembly.GetName().Name!)) { continue; }
            foreach (var reference in assembly.GetReferencedAssemblies().Where(name => name.Name!.StartsWith("IntoChat.OS", StringComparison.Ordinal)))
            { pending.Push(Assembly.Load(reference)); }
        }

        var leaks = seen.Where(name => Forbidden.Contains(name, StringComparer.Ordinal)
            || name == "IntoChat.OS.Authority" || name.StartsWith("IntoChat.OS.Authority.", StringComparison.Ordinal)).Order().ToArray();
        Assert.True(leaks.Length == 0, "The Sdk closure reaches: " + string.Join(", ", leaks));
        Assert.Contains("IntoChat.OS.Contracts.Configuration", seen);
        Assert.Contains("IntoChat.OS.Contracts.Compute", seen);
        Assert.Contains("IntoChat.OS.Sdk.Http", seen);
    }

    [Fact]
    public void TheSampleModuleGetsItsGeneratedShell()
    {
        var sample = Assembly.Load("IntoChat.OS.Sdk.SampleModule").GetType("IntoChat.OS.Sdk.SampleModule.SampleModule")!;
        Assert.NotNull(sample);
        var module = (Module)Activator.CreateInstance(sample)!;
        Assert.Equal("sample", module.Id.ToString());
        var integration = sample.GetProperty("Integration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? sample.GetField("Integration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var schema = Assert.IsType<IntoChat.OS.Sdk.Platform.Integrations.IntegrationSchema>(integration);
        Assert.Contains("ApiKey", schema.SecretFields);
    }
}
