using IntoChat.OS.Microsoft.CSharp;
using IntoChat.OS.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Microsoft.CSharp.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class BehaviorAssembliesFacts(CSharpHost host)
{
    [Fact]
    public async Task PutThenExistsThenReadUrlRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryBehaviorAssemblies();
        var compiled = new CSharpScriptCheck().Compile(new Dictionary<string, string> { ["a.cs"] = "Console.WriteLine(1);\n" }).Assemblies["a.cs"];

        Assert.False(await store.Exists(compiled.Hash, ct));
        await store.Put(compiled, ct);
        Assert.True(await store.Exists(compiled.Hash, ct));
        var url = await store.ReadUrl(compiled.Hash, TimeSpan.FromMinutes(5), ct);
        Assert.EndsWith("/" + compiled.Hash, url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileStoresEveryAssemblyAndReturnsItsHash()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var store = brain.Services.GetRequiredService<IBehaviorAssemblies>();
        var sandbox = brain.Services.GetRequiredService<IntoChat.OS.Apps.IScriptSandbox>();

        var check = await sandbox.Compile(new Dictionary<string, string> { ["synapses/x.cs"] = "Console.WriteLine(2);\n" }, ct);

        Assert.True(check.Success);
        Assert.True(await store.Exists(check.Hashes["synapses/x.cs"], ct));
    }

    [Fact]
    public async Task AHostWithoutDurableStorageKeepsAssembliesInProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        Assert.IsType<MemoryBehaviorAssemblies>(brain.Services.GetRequiredService<IBehaviorAssemblies>());
    }
}
