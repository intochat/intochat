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
}
