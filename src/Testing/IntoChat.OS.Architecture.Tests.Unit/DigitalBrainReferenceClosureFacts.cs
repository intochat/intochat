using System.Reflection;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A third party compiles against DigitalBrain alone (Phase 3 R4-07: the os Sdk is deleted; its legacy module base
// lives in the transitional os Framework until Phase 6). The closure of the DigitalBrain package must never reach
// any os assembly: not the runtime (Framework), the platform (Authority), the client or the vocabulary. Nor does it
// reach Orleans or ASP.NET Core (D-09): those belong to DigitalBrain.Runtime.Orleans and the hosts.
public sealed class DigitalBrainReferenceClosureFacts
{
    [Fact]
    public void AuthoringTypesLiveInTheDigitalBrainPackage()
    {
        var core = Assembly.Load("DigitalBrain");
        Assert.NotNull(core.GetType("DigitalBrain.Module", throwOnError: true));
        Assert.NotNull(core.GetType("DigitalBrain.ModuleManifest", throwOnError: true));
    }

    [Fact]
    public void DigitalBrainClosureExcludesEveryOsAssembly()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>([Assembly.Load("DigitalBrain")]);
        while (pending.Count > 0)
        {
            var assembly = pending.Pop();
            if (!seen.Add(assembly.GetName().Name!)) { continue; }
            named.UnionWith(assembly.GetReferencedAssemblies().Select(name => name.Name!));
            foreach (var reference in assembly.GetReferencedAssemblies().Where(name => name.Name!.StartsWith("DigitalBrain", StringComparison.Ordinal) || name.Name!.StartsWith("IntoChat", StringComparison.Ordinal)))
            { pending.Push(Assembly.Load(reference)); }
        }

        // Non-vacuous: the walk really started at the package.
        Assert.Contains("DigitalBrain", seen);
        var leaks = seen.Where(name => name.StartsWith("IntoChat", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(leaks.Length == 0, "The DigitalBrain closure reaches: " + string.Join(", ", leaks));
        var hosts = named.Where(name => name.StartsWith("Orleans", StringComparison.Ordinal) || name.StartsWith("Microsoft.Orleans", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(hosts.Length == 0, "The DigitalBrain closure references host assemblies: " + string.Join(", ", hosts));
    }

    [Fact]
    public void NoOsSdkProjectOrTemplatePackRemains()
    {
        Assert.DoesNotContain(Repository.Projects.Keys, name => name.StartsWith("IntoChat.OS." + "Sdk", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(Repository.Root, "templates", "IntoChat.OS.Templates")), "the os template pack is replaced by DigitalBrain.Templates (intochat-module)");
    }
}
