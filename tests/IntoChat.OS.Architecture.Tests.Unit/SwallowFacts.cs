using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

// QUAL-04 / House Rule #1: a failure is never swallowed silently. An empty catch is closed at zero.
// A broad catch (bare, or Exception) that neither logs nor rethrows, any discard of a caught exception
// (_ = ex), and any NullLogger usage in production code are all closed at zero. There is no baseline.
public sealed class SwallowFacts
{
    internal sealed record Site(int Line, bool Empty, bool BroadUnlogged, bool ExceptionDiscarded);

    internal static IReadOnlyList<Site> Walk(string source)
    {
        var sites = new List<Site>();
        foreach (var clause in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var empty = clause.Block.Statements.Count == 0 && clause.Filter is null && !IsCancellation(clause);
            var broad = clause.Declaration is null || clause.Declaration.Type.ToString() is "Exception" or "System.Exception";
            var name = clause.Declaration?.Identifier.Text;
            var discardSpans = name is { Length: > 0 }
                ? clause.Block.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(assign => assign.Left is IdentifierNameSyntax { Identifier.Text: "_" }
                        && IsExceptionDiscardExpression(assign.Right, name))
                    .Select(assign => assign.Span)
                    .ToArray()
                : [];
            var exceptionDiscarded = discardSpans.Length > 0;
            var observedInBlock = name is { Length: > 0 }
                && clause.Block.DescendantTokens().Any(token =>
                    token.Text == name && !discardSpans.Any(span => span.Contains(token.Span)));
            var logged = clause.Block.DescendantNodes()
                .Concat(clause.Filter?.DescendantNodes() ?? [])
                .OfType<InvocationExpressionSyntax>()
                .Any(call => MemberName(call).StartsWith("Log", StringComparison.Ordinal));
            var rethrows = clause.Block.DescendantNodes().Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
            var handled = observedInBlock || logged || rethrows || HasPositiveTypeNarrowingFilter(clause.Filter, name);
            sites.Add(new Site(
                clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                empty,
                broad && !empty && !handled,
                exceptionDiscarded));
        }
        return sites;
    }

    internal static IReadOnlyList<int> FindNullLoggerLines(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken)
                && token.Text is "NullLogger" or "NullLoggerFactory")
            .Select(token => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .Distinct()
            .ToArray();

    private static bool IsExceptionDiscardExpression(ExpressionSyntax expression, string name) => expression switch
    {
        ParenthesizedExpressionSyntax paren => IsExceptionDiscardExpression(paren.Expression, name),
        CastExpressionSyntax cast => IsExceptionDiscardExpression(cast.Expression, name),
        IdentifierNameSyntax id => id.Identifier.Text == name,
        MemberAccessExpressionSyntax access => IsExceptionDiscardExpression(access.Expression, name),
        ConditionalAccessExpressionSyntax conditional => IsExceptionDiscardExpression(conditional.Expression, name),
        InvocationExpressionSyntax call when call.Expression is MemberAccessExpressionSyntax access => IsExceptionDiscardExpression(access.Expression, name),
        _ => false,
    };

    private static bool HasPositiveTypeNarrowingFilter(CatchFilterClauseSyntax? filter, string? name)
    {
        if (filter is null || name is not { Length: > 0 })
        {
            return false;
        }

        foreach (var node in filter.FilterExpression.DescendantNodes())
        {
            if (node is IsPatternExpressionSyntax isPattern
                && isPattern.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == name
                && isPattern.Pattern is TypePatternSyntax typePattern
                && !IsBroadOrCancellationType(typePattern.Type.ToString())
                && !isPattern.Ancestors().Any(ancestor => ancestor is PrefixUnaryExpressionSyntax prefix && prefix.IsKind(SyntaxKind.LogicalNotExpression)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBroadOrCancellationType(string typeName)
        => typeName is "Exception" or "System.Exception"
            or "OperationCanceledException" or "System.OperationCanceledException"
            or "TaskCanceledException" or "System.Threading.Tasks.TaskCanceledException"
            or "OutOfMemoryException" or "System.OutOfMemoryException";

    // Cancellation is control flow, not a failure: a catch for OperationCanceledException alone may stay empty.
    private static bool IsCancellation(CatchClauseSyntax clause)
        => clause.Declaration?.Type.ToString() is "OperationCanceledException" or "System.OperationCanceledException" or "TaskCanceledException" or "System.Threading.Tasks.TaskCanceledException";

    private static string MemberName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
        IdentifierNameSyntax name => name.Identifier.Text,
        _ => "",
    };

    private static IEnumerable<string> ProductionFiles()
        => Repository.ProjectFiles("*.cs",
            _ => true,
            project => !project.Name.Contains(".Tests", StringComparison.Ordinal) && !project.Name.Contains(".Testing", StringComparison.Ordinal));

    private static List<(string Key, IReadOnlyList<Site> Sites)> Scan()
        => ProductionFiles().Select(path => (Repository.ProjectKey(path), Walk(File.ReadAllText(path)))).ToList();

    [Fact]
    public void TheWalkerFindsKnownCatches()
    {
        var sites = Walk(
            "class C { void M() { try { } catch { } try { } catch (System.Exception) { var x = 1; } try { } catch (System.Exception e) { logger.LogDebug(e, \"x\"); } try { } catch (System.Exception) { throw; } try { } catch (System.Exception error) { sink.TrySetException(error); } try { } catch (System.Exception ex) { _ = ex.Message; } try { } catch (System.Exception ex) when (ex is not System.OperationCanceledException) { return; } } }");
        Assert.Equal(7, sites.Count);
        Assert.True(sites[0].Empty);
        Assert.False(sites[0].BroadUnlogged);
        Assert.False(sites[0].ExceptionDiscarded);
        Assert.True(sites[1].BroadUnlogged);
        Assert.False(sites[2].BroadUnlogged);
        Assert.False(sites[3].BroadUnlogged);
        Assert.False(sites[4].BroadUnlogged);
        Assert.True(sites[5].ExceptionDiscarded);
        Assert.True(sites[5].BroadUnlogged);
        Assert.True(sites[6].BroadUnlogged);
    }

    [Fact]
    public void TheNullLoggerScannerFindsNullLogger()
    {
        Assert.Empty(FindNullLoggerLines("class C { void M(ILogger log) { log.LogDebug(\"ok\"); } }"));
        Assert.Equal([1], FindNullLoggerLines("class C { void M() { NullLogger.Instance.LogSwallowed(new Exception(), \"x\"); } }"));
    }

    [Fact]
    public void NoCatchIsEmpty()
    {
        var offenders = Scan().SelectMany(file => file.Sites.Where(site => site.Empty).Select(site => $"{file.Key}:{site.Line}")).ToArray();
        Assert.True(offenders.Length == 0,
            $"An empty catch hides a failure; log it with Swallow.LogSwallowed ({offenders.Length}): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoBroadCatchIsUnlogged()
    {
        var offenders = Scan().SelectMany(file => file.Sites.Where(site => site.BroadUnlogged).Select(site => $"{file.Key}:{site.Line}")).ToArray();
        Assert.True(offenders.Length == 0,
            "A broad catch must log (Swallow.LogSwallowed or a Log* call) or rethrow: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoExceptionIsDiscardedInCatch()
    {
        var offenders = Scan().SelectMany(file => file.Sites.Where(site => site.ExceptionDiscarded).Select(site => $"{file.Key}:{site.Line}")).ToArray();
        Assert.True(offenders.Length == 0,
            "A catch must never discard the caught exception with `_ = ex`; log it instead: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoNullLoggerInProduction()
    {
        var offenders = ProductionFiles()
            .SelectMany(path => FindNullLoggerLines(File.ReadAllText(path)).Select(line => $"{Repository.ProjectKey(path)}:{line}"))
            .ToArray();
        Assert.True(offenders.Length == 0,
            "Production code must never reference NullLogger or NullLoggerFactory: " + string.Join(", ", offenders));
    }
}
