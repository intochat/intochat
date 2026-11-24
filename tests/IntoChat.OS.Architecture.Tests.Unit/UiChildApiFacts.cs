using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

/// <summary>
/// Enforces owner decision P2: "slot" has one meaning (<c>[Slot]</c> dependency need);
/// UI child controls use <c>Child&lt;T&gt;</c>, <c>FollowChild</c>, <c>ReadChild</c>, and <c>SignalChild</c>.
/// </summary>
public sealed class UiChildApiFacts
{
    private static readonly HashSet<string> RetiredUiChildNames = new(StringComparer.Ordinal)
    {
        "FollowSlot",
        "ReadSlot",
        "SignalSlot",
    };

    [Fact]
    public void SlotIdentifierAppearsInAppsOnlyOnDeclaredSlotProperties()
    {
        var appsDir = Repository.ProjectNamed("IntoChat.OS.Apps").Directory;
        var files = Repository.SourceFiles(appsDir, "*.cs");
        Assert.NotEmpty(files);

        var ct = TestContext.Current.CancellationToken;
        var violations = new List<string>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file, cancellationToken: ct);
            var root = tree.GetCompilationUnitRoot(ct);

            foreach (var generic in root.DescendantNodes().OfType<GenericNameSyntax>())
            {
                var id = generic.Identifier.ValueText;
                if (id == "Slot" || RetiredUiChildNames.Contains(id))
                {
                    var line = tree.GetLineSpan(generic.Span, ct).StartLinePosition.Line + 1;
                    violations.Add($"{rel}:{line}: {generic}");
                }
            }

            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (RetiredUiChildNames.Contains(identifier.Identifier.ValueText))
                {
                    var line = tree.GetLineSpan(identifier.Span, ct).StartLinePosition.Line + 1;
                    violations.Add($"{rel}:{line}: {identifier}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} retired UI slot API usage(s) in src/IntoChat.OS.Apps (use Child/FollowChild/ReadChild/SignalChild instead):\n"
            + string.Join('\n', violations));
    }

    [Fact]
    public void StringDotCommandRouteIsRatchetedInApps()
    {
        const int StringDotCommandCap = 12;

        var appsDir = Repository.ProjectNamed("IntoChat.OS.Apps").Directory;
        var files = Repository.SourceFiles(appsDir, "*.cs");
        Assert.NotEmpty(files);

        var ct = TestContext.Current.CancellationToken;
        var hits = new List<string>();
        var customerResearcherHits = new List<string>();

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file, cancellationToken: ct);
            var root = tree.GetCompilationUnitRoot(ct);

            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeName = creation.Type.ToString();
                if (typeName != "Command" && !typeName.EndsWith(".Command", StringComparison.Ordinal))
                {
                    continue;
                }

                if (creation.ArgumentList is not { Arguments.Count: >= 2 } args)
                {
                    continue;
                }

                var firstArg = args.Arguments[0].Expression;
                if (firstArg is LiteralExpressionSyntax lit
                    && lit.IsKind(SyntaxKind.StringLiteralExpression)
                    && lit.Token.ValueText == ".")
                {
                    var line = tree.GetLineSpan(creation.Span, ct).StartLinePosition.Line + 1;
                    var entry = $"{rel}:{line}: {creation}";
                    hits.Add(entry);
                    if (rel.Contains("CustomerResearcher", StringComparison.OrdinalIgnoreCase))
                    {
                        customerResearcherHits.Add(entry);
                    }
                }
            }
        }

        Assert.True(
            customerResearcherHits.Count == 0,
            "CustomerResearcherApp must use typed Sends(...) instead of string Command(\".\", ...):\n"
            + string.Join('\n', customerResearcherHits));

        Assert.True(
            hits.Count <= StringDotCommandCap,
            $"String Command(\".\", ...) count in src/IntoChat.OS.Apps regressed to {hits.Count} (ratchet cap {StringDotCommandCap}); use typed Sends(new TSignal(...)) instead:\n"
            + string.Join('\n', hits));

        Assert.Equal(StringDotCommandCap, hits.Count);
    }
}
