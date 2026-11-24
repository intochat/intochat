using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

// Fast syntax-only house-rule checks (swallowed catches, wall-clock reads, ambient lookups) over loose C# sources.
internal static class SyntaxReviewer
{
    public static SyntaxReviewResult Review(SyntaxReview request, CancellationToken cancellationToken)
    {
        var findings = new List<SyntaxFinding>();
        foreach (var source in request.Sources ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source is null || string.IsNullOrEmpty(source.Text))
            {
                continue;
            }

            ReviewSource(source.Path, source.Text, findings, cancellationToken);
        }

        return new SyntaxReviewResult([.. findings]);
    }

    private static void ReviewSource(string path, string sourceText, List<SyntaxFinding> findings, CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: path, cancellationToken: cancellationToken);
        var root = tree.GetCompilationUnitRoot(cancellationToken);

        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (IsSwallowedCatch(catchClause))
            {
                findings.Add(new SyntaxFinding(
                    "HR-Swallow",
                    SyntaxFindingKind.Analyzer,
                    path,
                    LineOf(catchClause),
                    Blocking: true,
                    "Empty or silent catch block swallows exception (HR-Swallow / DB0022)."));
            }
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var receiver = memberAccess.Expression.ToString();
            var member = memberAccess.Name.Identifier.Text;

            if (receiver is "DateTime" or "System.DateTime" or "DateTimeOffset" or "System.DateTimeOffset"
                && member is "Now" or "UtcNow")
            {
                findings.Add(new SyntaxFinding(
                    "HR-12",
                    SyntaxFindingKind.ArchitectureFact,
                    path,
                    LineOf(memberAccess),
                    Blocking: false,
                    $"Direct wall-clock access '{receiver}.{member}'; inject TimeProvider instead (HR-12)."));
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name: GenericNameSyntax { Identifier.Text: "Get" },
                } ma)
            {
                var receiver = ma.Expression.ToString();
                if (receiver is "SiloNeurons" || receiver.EndsWith(".SiloNeurons", StringComparison.Ordinal))
                {
                    findings.Add(new SyntaxFinding(
                        "HR-23",
                        SyntaxFindingKind.ArchitectureFact,
                        path,
                        LineOf(invocation),
                        Blocking: true,
                        $"Ambient neuron lookup '{ma}'; declare a typed slot instead (HR-23)."));
                }
            }
            else if (invocation.Expression is GenericNameSyntax { Identifier.Text: "Get" } generic)
            {
                findings.Add(new SyntaxFinding(
                    "HR-23",
                    SyntaxFindingKind.ArchitectureFact,
                    path,
                    LineOf(invocation),
                    Blocking: false,
                    $"Ambient lookup '{generic}'; declare a typed slot instead (HR-23)."));
            }
        }
    }

    private static int LineOf(global::Microsoft.CodeAnalysis.SyntaxNode node)
        => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool IsSwallowedCatch(CatchClauseSyntax catchClause)
    {
        var statements = catchClause.Block.Statements;
        if (statements.Count == 0 || statements.All(static s => s is EmptyStatementSyntax))
        {
            return true;
        }

        var hasThrow = catchClause.Block.DescendantNodes().Any(static n => n is ThrowStatementSyntax or ThrowExpressionSyntax);
        if (hasThrow)
        {
            return false;
        }

        var hasLogging = catchClause.Block.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(static inv =>
            {
                var name = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => string.Empty,
                };
                return name.StartsWith("Log", StringComparison.Ordinal);
            });

        if (hasLogging)
        {
            return false;
        }

        var nonEmpty = statements.Where(static s => s is not EmptyStatementSyntax).ToArray();
        return nonEmpty.Length == 1
            && nonEmpty[0] is ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax;
    }
}
