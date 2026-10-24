using System.Text.RegularExpressions;

namespace IntoChat.OS.Kernel.Tests.Unit;

// Ruling 7: a secret goes only through the IForm secure window, and Ino's surfaces never echo exception text.
// InoCopy.cs (the strings file and Ino.Failure) is compiled into this project; the scans read the shipped sources.
public sealed partial class OsInoSecretFacts
{
    private static readonly string Sources = Path.Combine(SolutionProjects.Features, "os", "Ino");

    // An exception's message, or a router/agent error string, never reaches a status line, the composer's settle
    // or defer reason, or the app's presentation failure. Ino.Failure takes the exception only to log its type.
    [Fact]
    public void StatusNeverEchoesExceptionText()
    {
        var echoes = Scripts()
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(item => Echo().IsMatch(item.line))
            .Select(item => $"{Path.GetFileName(item.file)}:{item.index + 1}: {item.line.Trim()}")
            .ToArray();
        Assert.Empty(echoes);
    }

    [Fact]
    public void ComposerDraftIsNeverSetFromFormValues()
    {
        // Ino never reads a form: no script gets an IForm or touches its state, so no form value (and no secret
        // reference) can be what a composer Edit writes. IForm stays only in the model's contract list.
        var reads = Scripts()
            .Where(file => FormRead().IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Empty(reads);
    }

    [Fact]
    public void FailureReturnsTheCopyAndLogsOnlyTheExceptionType()
    {
        const string Secret = "fake-secret-value";
        var log = new StringWriter();
        var previous = Console.Error;
        string copy;
        Console.SetError(log);
        try { copy = Ino.Failure(InoCopy.StartFailed, new InvalidOperationException("refused " + Secret)); }
        finally { Console.SetError(previous); }

        Assert.Equal(InoCopy.StartFailed, copy);
        Assert.Contains(nameof(InvalidOperationException), log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FailureCopyNamesNoExceptionDetail()
    {
        foreach (var copy in InoCopy.Failures)
        {
            Assert.False(string.IsNullOrWhiteSpace(copy));
            Assert.DoesNotContain(": ", copy, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", copy, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> Scripts() => Directory.EnumerateFiles(Sources, "*.cs");

    // error.Message (or any caught exception's Message), and the router/agent error strings that carry it.
    [GeneratedRegex(@"\b(error|ex|exception)\.Message\b|\b(asked|routed|outcome|retained)\.Error\b")]
    private static partial Regex Echo();

    [GeneratedRegex(@"Get<IForm>|\bFormState\b|\bFormFieldState\b|\bFormSecret\b")]
    private static partial Regex FormRead();
}
