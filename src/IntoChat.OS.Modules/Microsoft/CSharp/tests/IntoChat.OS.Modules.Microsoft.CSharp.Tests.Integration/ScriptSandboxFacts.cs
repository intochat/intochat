using IntoChat.OS.Apps;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.CSharp;
using IntoChat.OS.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class ScriptSandboxFacts(CSharpHost host)
{
    [Fact]
    public async Task TheSandboxContractDiscoversComposedContractsAndReportsCompilationErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        Assert.True(sandbox.CanRun);
        var contracts = await AsOwner(brain, () => sandbox.ReadContracts(["csharp"], ct));
        Assert.Contains(contracts.Contracts, line => line.Contains(typeof(ICSharpFile).FullName!, StringComparison.Ordinal));
        var detail = await AsOwner(brain, () => sandbox.ReadContractDetail("csharp", typeof(ICSharpFile).FullName!, nameof(ICSharpFile.Inspect), ct));
        Assert.False(detail.Truncated);
        Assert.Contains("CSharpFileSnapshot(", Assert.Single(detail.Contracts), StringComparison.Ordinal);
        Assert.DoesNotContain("Write(", detail.Contracts[0], StringComparison.Ordinal);
        var invalid = await sandbox.Compile(new Dictionary<string, string> { ["app.cs"] = "Console.WriteLine(doesNotExist);" }, ct);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Errors, error => error.Id == "CS0103" && error.File == "app.cs");
        Assert.True((await sandbox.Compile(new Dictionary<string, string> { ["app.cs"] = "Console.WriteLine(1);" }, ct)).Success);
    }

    private static async Task<T> AsOwner<T>(ITestBrain brain, Func<Task<T>> read)
    {
        using (brain.Owner.AsCaller()) { return await read(); }
    }
}
