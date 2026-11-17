using IntoChat.OS.Modules.Apps.CodeBlocks;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// A code block is wrapped as a one-command app and compiled under the app boundary: safe code compiles, file access does not.
public sealed class CodeBlockSourceFacts
{
    private const string Good = """
        public static class Block
        {
            public static string Run(string input) => string.Join(" ", input.Split(' ').Reverse());
        }
        """;

    private const string ReadsAFile = """
        public static class Block
        {
            public static string Run(string input) => System.IO.File.ReadAllText("C:/Windows/win.ini");
        }
        """;

    [Fact]
    public void SafeCodeCompilesWithItsRunCommand()
    {
        var compiled = new AppPackageCompiler().Compile(CodeBlockSource.Package(CodeBlockSource.AppKey("r1"), "reverse", Good));
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));
        Assert.NotEmpty(compiled.AssemblyBytes);
    }

    [Fact]
    public void FileAccessDoesNotCompile()
        => Assert.False(new AppPackageCompiler().Compile(CodeBlockSource.Package(CodeBlockSource.AppKey("r2"), "steal", ReadsAFile)).Succeeded);

    [Fact]
    public void AStringResultReadsAsItsText() => Assert.Equal("c b a", CodeBlockSource.Output("\"c b a\""));

    private const string Orchestrates = """
        using System.Threading.Tasks;
        using IntoChat.Sdk;
        using IntoChat.OS.Modules.Apps.Runs;

        public static class Block
        {
            public static async Task<string> Run(string input, ISandboxBrain brain)
            {
                var runs = await brain.Get<IRuns>("runs/index").List(10);
                return $"{runs.Length} run(s)";
            }
        }
        """;

    [Fact]
    public void ABlockThatCallsTheBrainNeedsExactlyTheContractsItUses()
    {
        var compiled = new AppPackageCompiler().Compile(CodeBlockSource.Package(CodeBlockSource.AppKey("r3"), "count runs", Orchestrates));
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));
        Assert.Equal(["IntoChat.OS.Modules.Apps.Runs.IRuns"], CodeBlockNeeds.Infer(compiled.AssemblyBytes));
    }

    [Fact]
    public void ABlockWithoutBrainCallsNeedsNothing()
        => Assert.Empty(CodeBlockNeeds.Infer(new AppPackageCompiler().Compile(CodeBlockSource.Package(CodeBlockSource.AppKey("r4"), "reverse", Good)).AssemblyBytes));

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
