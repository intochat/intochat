using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Framework.Generators.Tests.Unit;

public sealed class CacheFacts
{
    private const string Module = """
        using IntoChat.OS.Framework.Modules;
        using IntoChat.OS.Framework.Modules;
        using IntoChat.OS.Framework.Modules;
        namespace Fixture;
        [Module("cached")]
        [Integration("cached", "Cached")]
        [Secret("ApiKey")]
        public sealed partial class CachedModule : Module;

        [ModuleHosting(typeof(CachedModule))]
        public sealed partial class CachedModuleHosting;
        """;

    private static readonly string[] Steps = ["ModuleModel", "ValidatedModuleModel", "HostingModel"];

    private static GeneratorDriver Run(GeneratorDriver driver, Compilation compilation)
        => driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

    [Fact]
    public void StepsAreCachedAndSourceIsReusedWhenAnUnrelatedTreeIsAdded()
    {
        var compilation = GeneratorHarness.Compile(Module);
        var first = Run(GeneratorHarness.CreateDriver(), compilation);
        var firstSources = first.GetRunResult().Results.Single().GeneratedSources;
        Assert.Equal(2, firstSources.Length);

        var changed = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Fixture; public sealed class Unrelated;", cancellationToken: TestContext.Current.CancellationToken));
        var second = Run(first, changed).GetRunResult().Results.Single();
        foreach (var step in Steps)
        {
            var outputs = second.TrackedSteps[step].Single().Outputs;
            Assert.NotEmpty(outputs);
            Assert.All(outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"{step}: {o.Reason}"));
        }

        foreach (var source in second.GeneratedSources)
        {
            var before = firstSources.Single(s => s.HintName == source.HintName);
            Assert.Same(before.SourceText, source.SourceText);
        }
    }

    [Fact]
    public void PositiveControlChangingTheModuleIdModifiesTheStep()
    {
        var compilation = GeneratorHarness.Compile(Module);
        var first = Run(GeneratorHarness.CreateDriver(), compilation);
        var edited = CSharpSyntaxTree.ParseText(Module.Replace("[Module(\"cached\")]", "[Module(\"changed\")]", StringComparison.Ordinal),
            cancellationToken: TestContext.Current.CancellationToken);
        var second = Run(first, compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), edited)).GetRunResult().Results.Single();
        var outputs = second.TrackedSteps["ModuleModel"].Single().Outputs;
        Assert.All(outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New, o.Reason.ToString()));
        Assert.Contains("\"changed\"", second.GeneratedSources.Single(s => s.HintName.Contains("CachedModule.g", StringComparison.Ordinal)).SourceText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingAnotherModulesOperationKeepsUnchangedGeneratedSource()
    {
        const string priced = """
            using IntoChat.OS.Framework.Modules;
            using IntoChat.OS.Framework.Modules;
            using IntoChat.OS.Connections.Compute;
            using IntoChat.OS.Contracts.Compute;
            using IntoChat.OS.Framework.Compute;
            using IntoChat.OS.Framework.Modules;
            namespace Pricing;
            public class Prices : IPriceList { public long? MicrosPerUnit(CostKind kind, string item) => 0; }
            [Module("priced"), Integration("api", "API"), ModulePriceList(typeof(Prices)), ExternalOperation("sample.read", "api", "request")]
            public sealed partial class PricedModule : Module;
            """;
        var compilation = GeneratorHarness.Compile(Module, priced);
        var first = Run(GeneratorHarness.CreateDriver(), compilation);
        var old = compilation.SyntaxTrees.Last();
        var replacement = CSharpSyntaxTree.ParseText(priced.Replace("sample.read", "sample.list", StringComparison.Ordinal), cancellationToken: TestContext.Current.CancellationToken);
        var second = Run(first, compilation.ReplaceSyntaxTree(old, replacement)).GetRunResult().Results.Single();
        var before = first.GetRunResult().Results.Single().GeneratedSources.Single(source => source.HintName == "Fixture.CachedModule.g.cs");
        var after = second.GeneratedSources.Single(source => source.HintName == "Fixture.CachedModule.g.cs");
        Assert.Same(before.SourceText, after.SourceText);
    }
}
