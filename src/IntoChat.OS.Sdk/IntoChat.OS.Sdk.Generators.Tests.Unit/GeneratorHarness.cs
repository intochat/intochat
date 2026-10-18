using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Sdk.Generators.Tests.Unit;

// Fixed reference set: TRUSTED_PLATFORM_ASSEMBLIES, the Sdk assembly (ModuleAttribute), the assembly that holds
// Framework.Modules.Module (Framework now; Sdk after 09-07, so goldens must stay byte-identical) and the Orleans
// assembly of ISiloBuilder. Fixture compilations must compile: no error diagnostics after generation.
internal static class GeneratorHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var extra = new[]
        {
            typeof(IntoChat.OS.Sdk.ModuleAttribute).Assembly.Location,
            typeof(IntoChat.OS.Framework.Modules.Module).Assembly.Location,
            typeof(Orleans.Hosting.ISiloBuilder).Assembly.Location,
        };
        return [.. trusted.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    });

    public static CSharpCompilation Compile(params string[] sources)
        => CSharpCompilation.Create("Fixture",
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver CreateDriver()
        => CSharpGeneratorDriver.Create(
            [new ModuleGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    public static void AssertNoErrors(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        var errors = compilation.GetDiagnostics().Concat(diagnostics).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    // LF-normalised golden compare; INTOCHATOS_UPDATE_GOLDENS=1 rewrites the golden.
    public static void AssertGolden(string goldenPath, string actual)
    {
        var normalised = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("INTOCHATOS_UPDATE_GOLDENS") == "1")
        {
            File.WriteAllText(goldenPath, normalised);
            return;
        }
        Assert.Equal(File.ReadAllText(goldenPath).Replace("\r\n", "\n", StringComparison.Ordinal), normalised);
    }
}
