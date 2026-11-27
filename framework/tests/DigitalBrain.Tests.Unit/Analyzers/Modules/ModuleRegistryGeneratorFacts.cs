using DigitalBrain.Analyzers.Modules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DigitalBrain.Analyzers.Tests.Unit.Modules;

public sealed class ModuleRegistryGeneratorFacts
{
    private const string FrameworkPrelude = """
        namespace DigitalBrain.Modules
        {
            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
            public sealed class ModuleAttribute(string id) : System.Attribute { public string Id { get; } = id; }

            public abstract class Module { }

            [System.AttributeUsage(System.AttributeTargets.Method, Inherited = false)]
            public sealed class GeneratedModuleRegistryAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class RegisteredModuleAttribute(System.Type moduleType) : System.Attribute { public System.Type ModuleType { get; } = moduleType; }
        }
        """;

    private static MetadataReference[] BaseReferences()
        =>
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Collections").Location),
        ];

    private static CSharpCompilation CompileAssembly(string name, string source, bool assertClean = true, params MetadataReference[] extraRefs)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [.. BaseReferences(), .. extraRefs],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (assertClean)
        {
            var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
            Assert.Empty(errors);
        }
        return compilation;
    }

    private static (GeneratorDriverRunResult Result, CSharpCompilation OutputCompilation) RunGenerator(CSharpCompilation compilation)
    {
        var driver = CSharpGeneratorDriver.Create(new ModuleRegistryGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var updatedCSharp = (CSharpCompilation)updated;
        var errors = updatedCSharp.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
        Assert.Empty(errors);
        return (driver.GetRunResult(), updatedCSharp);
    }

    [Fact]
    public void DiscoversModulesAcrossCurrentAndReferencedAssembliesAndSkipsNonModules()
    {
        var framework = CompileAssembly("DigitalBrain", FrameworkPrelude);
        var frameworkRef = framework.ToMetadataReference();

        var component = CompileAssembly(
            "Sample.Component",
            """
            using DigitalBrain.Modules;
            namespace Sample.Component
            {
                [Module("alpha")] public sealed class AlphaModule : Module { }
                [Module("abstract-skip")] public abstract class AbstractModule : Module { }
                public sealed class UnmarkedModule : Module { }
                [Module("no-base")] public sealed class NotAModule { }
                [Module("private-ctor")] public sealed class PrivateCtorModule : Module { private PrivateCtorModule() { } }
                [Module("internal-skip")] internal sealed class InternalModule : Module { }
            }
            """,
            assertClean: true,
            frameworkRef);

        var host = CompileAssembly(
            "Sample.Host",
            """
            using System;
            using System.Collections.Generic;
            using DigitalBrain.Modules;
            namespace Sample.Host
            {
                [Module("local")] public sealed class LocalModule : Module { }

                public static partial class HostCatalog
                {
                    [GeneratedModuleRegistry]
                    public static partial IReadOnlyList<Type> Discovered();
                }
            }
            """,
            assertClean: false,
            frameworkRef,
            component.ToMetadataReference());

        var (result, _) = RunGenerator(host);

        var registryTree = Assert.Single(result.GeneratedTrees, t => t.FilePath.EndsWith("HostCatalog.Discovered.g.cs", StringComparison.Ordinal));
        var registryText = registryTree.GetText(TestContext.Current.CancellationToken).ToString();
        Assert.Contains("typeof(global::Sample.Component.AlphaModule)", registryText, StringComparison.Ordinal);
        Assert.Contains("typeof(global::Sample.Host.LocalModule)", registryText, StringComparison.Ordinal);
        Assert.DoesNotContain("AbstractModule", registryText, StringComparison.Ordinal);
        Assert.DoesNotContain("UnmarkedModule", registryText, StringComparison.Ordinal);
        Assert.DoesNotContain("NotAModule", registryText, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateCtorModule", registryText, StringComparison.Ordinal);
        Assert.DoesNotContain("InternalModule", registryText, StringComparison.Ordinal);

        var registeredTree = Assert.Single(result.GeneratedTrees, t => t.FilePath.EndsWith("RegisteredModules.g.cs", StringComparison.Ordinal));
        var registeredText = registeredTree.GetText(TestContext.Current.CancellationToken).ToString();
        Assert.Contains("[assembly: global::DigitalBrain.Modules.RegisteredModuleAttribute(typeof(global::Sample.Host.LocalModule))]", registeredText, StringComparison.Ordinal);
        Assert.DoesNotContain("AlphaModule", registeredText, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsNothingWhenAssemblyDeclaresNoModulesAndNoRegistryMethod()
    {
        var framework = CompileAssembly("DigitalBrain", FrameworkPrelude);
        var plain = CompileAssembly("Sample.Plain", "public sealed class Plain { }", assertClean: true, framework.ToMetadataReference());

        var (result, _) = RunGenerator(plain);

        Assert.Empty(result.GeneratedTrees);
    }
}
