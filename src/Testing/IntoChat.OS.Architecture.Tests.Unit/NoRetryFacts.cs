using System.Text.RegularExpressions;

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

    [Theory]
    [InlineData("[Retry(3)]", true)]
    [InlineData("[RetryFact(MaxRetries = 2)]", true)]
    [InlineData("[Fact(Skip = \"later\")]", true)]
    [InlineData("[Theory(Skip = \"later\")]", true)]
    [InlineData("[Fact(Timeout = 5000)]", false)]
    [InlineData("[Fact]", false)]
    public void ThePatternFlagsRetriesAndSkips(string source, bool flagged) => Assert.Equal(flagged, Retried().IsMatch(source));
}
