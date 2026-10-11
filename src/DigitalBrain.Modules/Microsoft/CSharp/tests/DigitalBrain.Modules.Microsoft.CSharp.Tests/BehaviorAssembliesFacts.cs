using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class BehaviorAssembliesFacts
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
        await using var brain = await TestBrain.Create().WithModule<CSharpModule>().StartAsync(ct);
        var store = brain.Services.GetRequiredService<IBehaviorAssemblies>();
        var sandbox = brain.Services.GetRequiredService<DigitalBrain.Apps.IScriptSandbox>();

        var check = await sandbox.Compile(new Dictionary<string, string> { ["synapses/x.cs"] = "Console.WriteLine(2);\n" }, ct);

        Assert.True(check.Success);
        Assert.True(await store.Exists(check.Hashes["synapses/x.cs"], ct));
    }

    [Fact]
    public async Task AHostWithoutDurableStorageKeepsAssembliesInProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<CSharpModule>().StartAsync(ct);
        Assert.IsType<MemoryBehaviorAssemblies>(brain.Services.GetRequiredService<IBehaviorAssemblies>());
    }
}
