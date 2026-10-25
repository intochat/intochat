using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Sdk.Generators.Tests.Unit;

// Fixed reference set: TRUSTED_PLATFORM_ASSEMBLIES, the Sdk assembly (ModuleAttribute), the assembly that holds
// Framework.Modules.Module (Framework now; Sdk after 09-07, so goldens must stay byte-identical) and the Orleans
// assembly of ISiloBuilder and the Aspire hosting assembly of ModuleHosting<T>. Fixture compilations must compile: no error diagnostics after generation.
internal static class GeneratorHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var extra = new[]
        {
            typeof(IntoChat.OS.Sdk.ModuleAttribute).Assembly.Location,
            typeof(IntoChat.OS.Sdk.Http.HttpProblems).Assembly.Location,
            typeof(IntoChat.OS.Sdk.Modules.Module).Assembly.Location,
            typeof(Orleans.Hosting.ISiloBuilder).Assembly.Location,
            typeof(IntoChat.OS.Aspire.Hosting.IIntoChatOSModuleHosting).Assembly.Location,
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

    // Generated sources in hint-name order, each under a header, so one golden holds a whole case.
    public static string Render(GeneratorDriver driver)
        => string.Concat(driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources)
            .OrderBy(source => source.HintName, StringComparer.Ordinal)
            .Select(source => "// ==== " + source.HintName + " ====\n" + source.SourceText.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)));

    // Goldens are copied beside the test assembly, so reads never depend on source paths (deterministic builds map them to /_/).
    public static string GoldenDirectory() => Path.Combine(AppContext.BaseDirectory, "Goldens");

    public static string[] SampleSources() => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "SampleSources"), "*.cs")
        .Order(StringComparer.Ordinal).Select(File.ReadAllText).ToArray();

    // Regeneration writes back to the source tree, found by walking up to the solution file.
    private static string SourceGoldenDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx")))
            {
                var project = System.Xml.Linq.XDocument.Load(Path.Combine(directory.FullName, "Intochat.OS.slnx")).Descendants("Project")
                    .Select(element => (string)element.Attribute("Path")!)
                    .Single(path => path.EndsWith("IntoChat.OS.Sdk.Generators.Tests.Unit.csproj", StringComparison.Ordinal));
                return Path.Combine(directory.FullName, Path.GetDirectoryName(project)!, "Goldens");
            }
        }
        throw new InvalidOperationException("Intochat.OS.slnx not found above " + AppContext.BaseDirectory);
    }

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
            File.WriteAllText(Path.Combine(SourceGoldenDirectory(), Path.GetFileName(goldenPath)), normalised);
            return;
        }
        Assert.Equal(File.ReadAllText(goldenPath).Replace("\r\n", "\n", StringComparison.Ordinal), normalised);
    }
}
