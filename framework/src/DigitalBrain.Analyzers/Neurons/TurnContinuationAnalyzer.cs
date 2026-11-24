using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DigitalBrain.Analyzers.Neurons;

/// <summary>
/// DB0050: <c>ConfigureAwait(false)</c> (or <c>ConfigureAwaitOptions</c> without <c>ContinueOnCapturedContext</c>) lexically inside
/// a class that derives from <c>Neuron</c>, or a type marked <c>[TurnBound]</c> in the kernel. On Orleans the rest of the method then
/// runs on the thread pool, off the activation's scheduler, beside whatever interleaves with the turn. A non-constant argument is
/// not reported. Code in plain services outside a turn is not reported.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TurnContinuationAnalyzer : DiagnosticAnalyzer
{
    private const int ContinueOnCapturedContext = 1;

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.TurnContinuation);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name != "ConfigureAwait" || invocation.Arguments.Length == 0) { return; }
        if (!IsAwaitableType(method.ContainingType)) { return; }
        var argument = invocation.Arguments[invocation.Arguments.Length - 1].Value;
        if (!LeavesTheContext(argument)) { return; }
        if (!InsideATurn(context.ContainingSymbol)) { return; }
        if (invocation.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } syntax) { return; }
        var location = Location.Create(syntax.SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(access.OperatorToken.SpanStart, syntax.Span.End));
        context.ReportDiagnostic(Diagnostic.Create(Rules.TurnContinuation, location, context.ContainingSymbol.ContainingType?.Name ?? context.ContainingSymbol.Name));
    }

    private static bool IsAwaitableType(INamedTypeSymbol? type)
    {
        var name = Rules.FullName(type?.OriginalDefinition);
        return name is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1"
            or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1"
            or "System.Threading.Tasks.TaskAsyncEnumerableExtensions";
    }

    private static bool LeavesTheContext(IOperation argument)
    {
        var constant = argument.ConstantValue;
        if (!constant.HasValue) { return false; }
        return constant.Value switch
        {
            bool continueOnCapturedContext => !continueOnCapturedContext,
            int options => (options & ContinueOnCapturedContext) == 0,
            _ => false,
        };
    }

    private static bool InsideATurn(ISymbol symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (Rules.DerivesFrom(type, Rules.NeuronType) || Rules.Has(type, Rules.TurnBoundAttribute)) { return true; }
        }
        return false;
    }
}
