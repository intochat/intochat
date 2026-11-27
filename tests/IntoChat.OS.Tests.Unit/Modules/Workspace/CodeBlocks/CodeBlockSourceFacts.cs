using IntoChat.OS.Modules.Apps.CodeBlocks;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// Plane 1 orchestration scripts are wrapped as a one-command app and compiled under the app boundary: safe code compiles, file access does not.
public sealed class CodeBlockSourceFacts
{
    private const string Good = """
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                => Task.FromResult(new OrchestrationResult("succeeded", string.Join(" ", inputJson.Split(' ').Reverse())));
        }
        """;

    private const string ReadsAFile = """
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                => Task.FromResult(new OrchestrationResult("succeeded", System.IO.File.ReadAllText("C:/Windows/win.ini")));
        }
        """;

    [Fact]
    public void SafeCodeCompilesWithItsOrchestrateCommand()
    {
        var compiled = new AppPackageCompiler().Compile(CodeOrchestrationScriptWrapper.BuildPackage(CodeOrchestrationScriptWrapper.AppKey("r1"), "reverse", Good));
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));
        Assert.NotEmpty(compiled.AssemblyBytes);
    }

    [Fact]
    public void FileAccessDoesNotCompile()
        => Assert.False(new AppPackageCompiler().Compile(CodeOrchestrationScriptWrapper.BuildPackage(CodeOrchestrationScriptWrapper.AppKey("r2"), "steal", ReadsAFile)).Succeeded);

    [Fact]
    public void ResultJsonParsesSummary()
        => Assert.Equal("c b a", CodeOrchestrationScriptWrapper.ParseResultJson("{\"status\":\"succeeded\",\"summary\":\"c b a\",\"artifacts\":[],\"metrics\":{}}").Summary);

    private const string Orchestrates = """
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;
        using IntoChat.OS.Modules.Apps.Runs;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain brain, string inputJson, CancellationToken ct)
            {
                var runs = await brain.Get<IRuns>("runs/index").List(10);
                return new OrchestrationResult("succeeded", $"{runs.Length} run(s)");
            }
        }
        """;

    [Fact]
    public void AScriptThatCallsTheBrainNeedsExactlyTheContractsItUses()
    {
        var compiled = new AppPackageCompiler().Compile(CodeOrchestrationScriptWrapper.BuildPackage(CodeOrchestrationScriptWrapper.AppKey("r3"), "count runs", Orchestrates));
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));
        Assert.Equal(["IntoChat.OS.Modules.Apps.Runs.IRuns"], CodeBlockNeeds.Infer(compiled.AssemblyBytes));
    }

    [Fact]
    public void AScriptWithoutBrainCallsNeedsNothing()
        => Assert.Empty(CodeBlockNeeds.Infer(new AppPackageCompiler().Compile(CodeOrchestrationScriptWrapper.BuildPackage(CodeOrchestrationScriptWrapper.AppKey("r4"), "reverse", Good)).AssemblyBytes));

    [Fact]
    public void TheNeedsHashIgnoresOrder()
        => Assert.Equal(CodeBlockNeeds.Hash(["b", "a"]), CodeBlockNeeds.Hash(["a", "b"]));

    [Theory]
    [InlineData("DigitalBrain.Confirmation.IConfirmations")]
    [InlineData("IntoChat.OS.Modules.Apps.CodeBlocks.IClientGrants")]
    [InlineData("DigitalBrain.Capabilities.Identity.IGrants")]
    public void AuthorityContractsAreNeverAllowedNeeds(string contract)
        => Assert.Equal([contract], CodeBlockNeeds.Forbidden([contract, "IntoChat.OS.Modules.Apps.Runs.IRuns"]));
}
