using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Files;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpScriptCheckFacts
{
    private readonly CSharpScriptCheck _check = new();

    [Fact]
    public void ACompilingScriptWithContractsPasses()
    {
        var result = _check.Compile(new Dictionary<string, string>
        {
            ["synapses/hello.cs"] = """
                var file = default(DigitalBrain.Microsoft.CSharp.Files.ICSharpFile);
                Console.WriteLine($"{args.Length} {file}");
                await Task.CompletedTask;
                """,
        });

        Assert.True(result.Success, string.Join("; ", result.Errors.Select(error => $"{error.Id} {error.Message}")));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ACompilingScriptEmitsAnAssemblyNamedByItsContentHash()
    {
        var result = _check.Compile(new Dictionary<string, string>
        {
            ["synapses/hello.cs"] = "Console.WriteLine(args.Length);\nawait Task.CompletedTask;\n",
        });

        Assert.True(result.Success, string.Join("; ", result.Errors.Select(error => $"{error.Id} {error.Message}")));
        var compiled = result.Assemblies["synapses/hello.cs"];
        Assert.NotEmpty(compiled.Assembly);
        Assert.NotEmpty(compiled.Symbols);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(compiled.Assembly)), compiled.Hash);
        Assert.Equal("Program", System.Reflection.Assembly.Load(compiled.Assembly).EntryPoint!.DeclaringType!.Name);
    }

    [Fact]
    public void TheSameSourceCompilesToTheSameHash()
    {
        var files = new Dictionary<string, string> { ["a.cs"] = "Console.WriteLine(1);\n" };
        Assert.Equal(_check.Compile(files).Assemblies["a.cs"].Hash, _check.Compile(files).Assemblies["a.cs"].Hash);
    }

    [Fact]
    public void AFailingScriptEmitsNothing()
    {
        var result = _check.Compile(new Dictionary<string, string> { ["bad.cs"] = "int wrong = \"text\";\n" });
        Assert.False(result.Success);
        Assert.Empty(result.Assemblies);
    }

    [Fact]
    public void ATypeErrorNamesTheFileTheLineAndTheDiagnostic()
    {
        var result = _check.Compile(new Dictionary<string, string>
        {
            ["tests.cs"] = "var ok = 1;\nint wrong = \"text\";\n",
        });

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("tests.cs", "CS0029", 2), (error.File, error.Id, error.Line));
    }

    [Fact]
    public void CompilingNothingIsRefused()
        => Assert.Throws<ArgumentException>(() => _check.Compile(new Dictionary<string, string>()));
}
