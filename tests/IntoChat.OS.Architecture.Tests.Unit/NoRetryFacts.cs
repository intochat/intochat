using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A fact that passes on a second try hides the race it lost. Flaky facts are root-caused with a deterministic seam
// (a gate, a counted signal, a fake clock), never retried, skipped or given a longer wait; the list of exceptions is closed at zero.
public sealed partial class NoRetryFacts
{
    [GeneratedRegex(@"\[\s*(?:Retry\w*|\w*RetryFact|\w*RetryTheory)\b|\[\s*(?:Fact|Theory)\s*\([^\]]*\bSkip\s*=")]
    private static partial Regex Retried();

    [Fact]
    public void NoTestIsRetriedOrSkipped()
    {
        var problems = new List<string>();
        foreach (var project in Repository.TestProjects())
        {
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs", path => !path.EndsWith("NoRetryFacts.cs", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(file);
                if (Retried().IsMatch(text)) { problems.Add(Repository.Relative(file)); }
            }
        }
        Assert.True(problems.Count == 0, "Root-cause the flake instead of retrying or skipping it:\n" + string.Join('\n', problems));
    }

    // House Rule #22: tests never silently `return;` after an OperatingSystem / IsOSPlatform guard; use Assert.SkipUnless/SkipWhen so the skip is reported.
    [Fact]
    public void NoTestSilentlyReturnsOnOsPlatformCheck()
    {
        var offenders = new List<string>();
        foreach (var project in Repository.TestProjects())
        {
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs", path => !path.EndsWith("NoRetryFacts.cs", StringComparison.Ordinal)))
            {
                foreach (var line in FindSilentOsReturnLines(File.ReadAllText(file)))
                {
                    offenders.Add($"{Repository.ProjectKey(file)}:{line}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "A test must never silently return on an OS platform guard; call Assert.SkipUnless/Assert.SkipWhen:\n" + string.Join('\n', offenders));
    }

    internal static IReadOnlyList<int> FindSilentOsReturnLines(string source)
    {
        var lines = new List<int>();
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var isTest = method.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attr => SourceTypes.AttributeName(attr) is "Fact" or "Theory" or "SkippableFact" or "SkippableTheory");
            if (!isTest || method.Body is null) { continue; }
            foreach (var branch in method.Body.DescendantNodes().OfType<IfStatementSyntax>())
            {
                var cond = branch.Condition.ToString();
                if (!cond.Contains("OperatingSystem.Is", StringComparison.Ordinal)
                    && !cond.Contains("RuntimeInformation.IsOSPlatform", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (var ret in branch.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>())
                {
                    if (ret.Ancestors().TakeWhile(a => a != branch).Any(a => a is LambdaExpressionSyntax or LocalFunctionStatementSyntax))
                    {
                        continue;
                    }
                    var priorSkip = branch.Statement.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(inv => inv.SpanStart < ret.SpanStart && inv.Expression.ToString().Contains("Assert.Skip", StringComparison.Ordinal));
                    if (!priorSkip)
                    {
                        lines.Add(ret.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    }
                }
            }
        }
        return lines;
    }

    [Theory]
    [InlineData("[Retry(3)]", true)]
    [InlineData("[RetryFact(MaxRetries = 2)]", true)]
    [InlineData("[Fact(Skip = \"later\")]", true)]
    [InlineData("[Theory(Skip = \"later\")]", true)]
    [InlineData("[Fact(Timeout = 5000)]", false)]
    [InlineData("[Fact]", false)]
    public void ThePatternFlagsRetriesAndSkips(string source, bool flagged) => Assert.Equal(flagged, Retried().IsMatch(source));

    [Fact]
    public void TheOsGuardScannerFlagsSilentReturnsAndAllowsAssertSkip()
    {
        Assert.NotEmpty(FindSilentOsReturnLines("class C { [Fact] void T() { if (!OperatingSystem.IsWindows()) { return; } } }"));
        Assert.Empty(FindSilentOsReturnLines("class C { [Fact] void T() { Assert.SkipUnless(OperatingSystem.IsWindows(), \"win\"); } }"));
        Assert.Empty(FindSilentOsReturnLines("class C { [Fact] void T() { if (!OperatingSystem.IsWindows()) { Assert.Skip(\"win\"); return; } } }"));
    }
}
