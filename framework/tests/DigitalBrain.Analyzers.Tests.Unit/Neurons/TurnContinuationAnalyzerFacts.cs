using DigitalBrain.Analyzers.CodeFixes;
using DigitalBrain.Analyzers.Neurons;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DigitalBrain.Analyzers.Tests.Unit.Neurons;

public sealed class TurnContinuationAnalyzerFacts
{
    private static Task Diagnostics(string source) => Verify<TurnContinuationAnalyzer>.Diagnostics(source);

    [Fact]
    public void IsAnErrorOnByDefault()
    {
        Assert.Equal("DB0050", Rules.TurnContinuation.Id);
        Assert.Equal(DiagnosticSeverity.Error, Rules.TurnContinuation.DefaultSeverity);
        Assert.True(Rules.TurnContinuation.IsEnabledByDefault);
    }

    [Fact]
    public Task ReportsConfigureAwaitFalseOnEveryAwaitableInANeuron() => Diagnostics("""
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using DigitalBrain;
        public sealed class Memory : Neuron
        {
            public async Task<int> Recall(Task<int> read, ValueTask write, IAsyncEnumerable<int> notes)
            {
                await Task.Delay(1){|DB0050:.ConfigureAwait(false)|};
                await write{|DB0050:.ConfigureAwait(false)|};
                await foreach (var note in notes{|DB0050:.ConfigureAwait(false)|}) { }
                await Task.Yield();
                return await read{|DB0050:.ConfigureAwait(continueOnCapturedContext: false)|};
            }
        }
        """);

    [Fact]
    public Task ReportsThroughABaseClassANestedTypeAndALambda() => Diagnostics("""
        using System;
        using System.Threading.Tasks;
        using DigitalBrain;
        public abstract class AiNeuron<TState> : Neuron where TState : class { }
        public sealed record MemoryState;
        public sealed class Memory : AiNeuron<MemoryState>
        {
            private readonly Func<Task> _later = async () => await Task.Delay(1){|DB0050:.ConfigureAwait(false)|};

            public async Task Remember() => await Task.Delay(1){|DB0050:.ConfigureAwait(false)|};

            private sealed class Sync
            {
                public async Task Run() => await Task.Delay(1){|DB0050:.ConfigureAwait(false)|};
            }
        }
        """);

    [Fact]
    public Task ReportsConfigureAwaitOptionsThatLeaveTheContext() => Diagnostics("""
        using System.Threading.Tasks;
        using DigitalBrain;
        public sealed class Memory : Neuron
        {
            public async Task Remember()
            {
                await Task.Delay(1){|DB0050:.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)|};
                await Task.Delay(1).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        """);

    [Fact]
    public Task StayingOnTheTurnAndCodeOutsideATurnAreNotReported() => Diagnostics("""
        using System.Threading.Tasks;
        using DigitalBrain;
        public sealed class Memory : Neuron
        {
            public async Task Remember(bool flag)
            {
                await Task.Delay(1);
                await Task.Delay(1).ConfigureAwait(true);
                await Task.Delay(1).ConfigureAwait(flag);
            }
        }
        public sealed class Embeddings
        {
            public async Task Index() => await Task.Delay(1).ConfigureAwait(false);
        }
        """);

    // The framework's kernel code a neuron's turn runs through (subscribing, delivery) is marked [TurnBound].
    [Fact]
    public Task ReportsInsideATurnBoundType() => Diagnostics("""
        using System.Threading.Tasks;
        namespace DigitalBrain.Kernel
        {
            [System.AttributeUsage(System.AttributeTargets.Class)] internal sealed class TurnBoundAttribute : System.Attribute;

            [TurnBound]
            internal sealed class Delivery
            {
                public async Task Nudged() => await Task.Delay(1){|DB0050:.ConfigureAwait(false)|};
            }

            internal sealed class Journal
            {
                public async Task Append() => await Task.Delay(1).ConfigureAwait(false);
            }
        }
        """);

    [Fact]
    public async Task TheFixStaysOnTheTurn()
    {
        var fixedSource = await ApplyFix("""
            using System.Threading.Tasks;
            using DigitalBrain;
            public sealed class Memory : Neuron
            {
                public async Task<int> Recall(Task<int> read)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    return await read.ConfigureAwait(continueOnCapturedContext: false);
                }
            }
            """);

        Assert.Equal("""
            using System.Threading.Tasks;
            using DigitalBrain;
            public sealed class Memory : Neuron
            {
                public async Task<int> Recall(Task<int> read)
                {
                    await Task.Delay(1).ConfigureAwait(true);
                    return await read.ConfigureAwait(continueOnCapturedContext: true);
                }
            }
            """, fixedSource.ReplaceLineEndings("\n"));
    }

    private static async Task<string> ApplyFix(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        var references = await Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net80.ResolveAsync(LanguageNames.CSharp, ct);
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Fix", "Fix", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: references));
        project = project.AddDocument("Stubs.cs", SourceText.From(Verify<TurnContinuationAnalyzer>.Stubs)).Project;
        var document = project.AddDocument("Memory.cs", SourceText.From(source));
        for (var round = 0; round < 10; round++)
        {
            var compilation = (await document.Project.GetCompilationAsync(ct))!;
            var diagnostics = await compilation.WithAnalyzers([new TurnContinuationAnalyzer()]).GetAnalyzerDiagnosticsAsync(ct);
            var diagnostic = diagnostics.OrderBy(d => d.Location.SourceSpan.Start).FirstOrDefault();
            if (diagnostic is null) { break; }
            var actions = new List<CodeAction>();
            await new TurnContinuationFix().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), ct));
            var operations = await Assert.Single(actions).GetOperationsAsync(ct);
            document = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetDocument(document.Id)!;
        }
        return (await document.GetTextAsync(ct)).ToString();
    }
}
