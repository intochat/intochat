using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Fast, local Roslyn syntax self-review run over changed C# files in a worktree
/// before the approval card is presented to the owner.
/// </summary>
internal sealed class LocalSelfReview
{
    public static SelfReviewReport ReviewWorktree(string worktreePath, IReadOnlyList<string> changedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var findings = new List<ReviewFinding>();

        foreach (var rawPath in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(rawPath)
                || !rawPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedPath = rawPath.Replace('\\', '/');
            var fullPath = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(worktreePath, rawPath);

            if (!File.Exists(fullPath))
            {
                continue;
            }

            var sourceText = File.ReadAllText(fullPath);
            ReviewSource(normalizedPath, sourceText, findings);
        }

        return new SelfReviewReport([.. findings]);
    }

    private static void ReviewSource(string path, string sourceText, List<ReviewFinding> findings)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: path);
        var root = tree.GetCompilationUnitRoot();

        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (IsSwallowedCatch(catchClause))
            {
                var line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new ReviewFinding(
                    RuleId: "HR-Swallow",
                    Source: ReviewFindingSource.Analyzer,
                    Path: path,
                    Line: line,
                    Severity: ReviewSeverity.Blocking,
                    Message: "Empty or silent catch block swallows exception (HR-Swallow / DB0022)."));
            }
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var receiver = memberAccess.Expression.ToString();
            var member = memberAccess.Name.Identifier.Text;

            if (receiver is "DateTime" or "System.DateTime" or "DateTimeOffset" or "System.DateTimeOffset"
                && member is "Now" or "UtcNow")
            {
                var line = memberAccess.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new ReviewFinding(
                    RuleId: "HR-12",
                    Source: ReviewFindingSource.ArchitectureFact,
                    Path: path,
                    Line: line,
                    Severity: ReviewSeverity.Warning,
                    Message: $"Direct wall-clock access '{receiver}.{member}'; inject TimeProvider instead (HR-12)."));
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
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    findings.Add(new ReviewFinding(
                        RuleId: "HR-23",
                        Source: ReviewFindingSource.ArchitectureFact,
                        Path: path,
                        Line: line,
                        Severity: ReviewSeverity.Blocking,
                        Message: $"Ambient neuron lookup '{ma}'; declare a typed slot instead (HR-23)."));
                }
            }
            else if (invocation.Expression is GenericNameSyntax { Identifier.Text: "Get" } generic)
            {
                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new ReviewFinding(
                    RuleId: "HR-23",
                    Source: ReviewFindingSource.ArchitectureFact,
                    Path: path,
                    Line: line,
                    Severity: ReviewSeverity.Warning,
                    Message: $"Ambient lookup '{generic}'; declare a typed slot instead (HR-23)."));
            }
        }
    }

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
