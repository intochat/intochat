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
        var appsDir = Path.Combine(Repository.Root, "src", "IntoChat.OS.Apps");
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
}
