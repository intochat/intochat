using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Sdk.Generators.Tests.Unit;

public sealed class SpikeFacts
{
    private const string Module = """
        using IntoChat.OS.Sdk;
        namespace Fixture;
        [Module("spike")]
        public sealed class SpikeModule;
        """;

    [Fact]
    public void ModuleModelStepIsCachedWhenAnUnrelatedTreeIsAdded()
    {
        var compilation = GeneratorHarness.Compile(Module);
        var driver = GeneratorHarness.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        GeneratorHarness.AssertNoErrors(output, diagnostics);
        Assert.Single(driver.GetRunResult().GeneratedTrees);

        var changed = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Fixture; public sealed class Unrelated;", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(changed, TestContext.Current.CancellationToken);
        var step = driver.GetRunResult().Results.Single().TrackedSteps["ModuleModel"].Single();
        Assert.All(step.Outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, o.Reason.ToString()));
    }

    [Fact]
    public void ModuleModelStepIsNewWhenTheModuleChanges()
    {
        var compilation = GeneratorHarness.Compile(Module);
        var driver = GeneratorHarness.CreateDriver().RunGenerators(compilation, TestContext.Current.CancellationToken);
        var edited = CSharpSyntaxTree.ParseText(Module.Replace("SpikeModule", "OtherModule", StringComparison.Ordinal), cancellationToken: TestContext.Current.CancellationToken);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), edited), TestContext.Current.CancellationToken);
        var step = driver.GetRunResult().Results.Single().TrackedSteps["ModuleModel"].Single();
        Assert.All(step.Outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New, o.Reason.ToString()));
    }
}
