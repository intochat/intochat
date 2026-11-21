using System.Security;
using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

public sealed class WorktreeRoslynAnalyzerFacts : IDisposable
{
    private readonly string _root;

    public WorktreeRoslynAnalyzerFacts()
    {
        _root = Path.Combine(Path.GetTempPath(), "roslyn-wt-facts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "code"));
        File.WriteAllText(Path.Combine(_root, "code", "MathOps.cs"), """
            namespace Sample.Domain;

            public sealed class MathOps
            {
                public int Add(int a, int b) => a + b;
            }

            public sealed record Point2D(int X, int Y);
            """);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task InspectsSymbolsAppliesAstValidatedEditsAndDetectsSemanticDiagnostics()
    {
        var roslyn = new WorktreeRoslynAnalyzer();

        var initialSymbols = await roslyn.InspectSymbolsAsync(_root, ct: Ct);
        Assert.Equal(2, initialSymbols.Count);
        Assert.Contains(initialSymbols, s => s.Name == "MathOps" && s.Namespace == "Sample.Domain" && s.Kind == "class" && s.Members.Contains("Add"));
        Assert.Contains(initialSymbols, s => s.Name == "Point2D" && s.Kind == "record");

        var editResult = await roslyn.ApplyEditsAsync(
            _root,
            [
                new WorktreeCSharpEdit(
                    WorktreeCSharpEditKind.AddUsing,
                    "code/MathOps.cs",
                    NamespaceName: "System.Globalization"),
                new WorktreeCSharpEdit(
                    WorktreeCSharpEditKind.AddOrReplaceMember,
                    "code/MathOps.cs",
                    TypeName: "MathOps",
                    MemberName: "Multiply",
                    Source: "public int Multiply(int a, int b) => a * b;"),
                new WorktreeCSharpEdit(
                    WorktreeCSharpEditKind.AddOrReplaceMember,
                    "code/MathOps.cs",
                    TypeName: "Point2D",
                    MemberName: "Manhattan",
                    Source: "public int Manhattan => System.Math.Abs(X) + System.Math.Abs(Y);"),
                new WorktreeCSharpEdit(
                    WorktreeCSharpEditKind.CreateTypeFile,
                    "code/SummaryFormatter.cs",
                    NamespaceName: "Sample.Domain",
                    TypeName: "SummaryFormatter",
                    TypeKind: "class",
                    Source: "public static string Format(int v) => v.ToString();"),
            ],
            Ct);

        Assert.True(editResult.Succeeded, editResult.Detail);
        Assert.Equal(["code/MathOps.cs", "code/SummaryFormatter.cs"], editResult.TouchedFiles);
        Assert.Contains("+    public int Multiply(int a, int b)", editResult.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain(editResult.Diagnostics, static d => d.Severity == "Error");

        // Invalid syntax member is rejected before touching disk
        var badSyntax = await roslyn.ApplyEditsAsync(
            _root,
            [
                new WorktreeCSharpEdit(
                    WorktreeCSharpEditKind.AddOrReplaceMember,
                    "code/MathOps.cs",
                    TypeName: "MathOps",
                    Source: "public int Broken( => ;"),
            ],
            Ct);
        Assert.False(badSyntax.Succeeded);

        // Semantic error in C# file is surfaced by AnalyzeAsync
        await File.WriteAllTextAsync(Path.Combine(_root, "code", "BrokenSemantic.cs"), """
            namespace Sample.Domain;

            public sealed class BrokenSemantic
            {
                public int Compute() => UndeclaredSymbolXYZ + 1;
            }
            """, Ct);

        var semanticDiags = await roslyn.AnalyzeAsync(_root, ["code/BrokenSemantic.cs"], Ct);
        Assert.Contains(semanticDiags, static d => d.Id == "CS0103" && d.Severity == "Error");
    }

    [Fact]
    public async Task RejectsTraversalDotfilesAndNonCSharpPaths()
    {
        var roslyn = new WorktreeRoslynAnalyzer();

        await Assert.ThrowsAsync<SecurityException>(() =>
            roslyn.AnalyzeAsync(_root, ["../escape.cs"], Ct));
        await Assert.ThrowsAsync<SecurityException>(() =>
            roslyn.AnalyzeAsync(_root, [".git/HEAD.cs"], Ct));
        await Assert.ThrowsAsync<SecurityException>(() =>
            roslyn.ApplyEditsAsync(_root, [new WorktreeCSharpEdit(WorktreeCSharpEditKind.AddUsing, "README.md", NamespaceName: "System")], Ct));
    }
}
