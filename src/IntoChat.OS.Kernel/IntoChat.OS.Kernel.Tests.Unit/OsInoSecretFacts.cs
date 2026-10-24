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

    // Every status line and menu detail Ino writes is InoCopy (or InoModels/InoLayout fixed copy, or a model's label):
    // no string literal or interpolation is passed to a status write or a Detail, so no runtime text can leak in.
    [Fact]
    public void StatusAndDetailTextIsFixedCopy()
    {
        var literals = Scripts()
            .SelectMany(file => StatusWrite().Matches(File.ReadAllText(file)).Select(match => (file, match)))
            .Where(item => Literal().IsMatch(item.match.Groups["arg"].Value))
            .Select(item => $"{Path.GetFileName(item.file)}: {item.match.Value.Trim()}")
            .ToArray();
        Assert.Empty(literals);
    }

    // A caught exception's text (Message, InnerException, ToString or an interpolation of it) is never used in its
    // catch body: the exception goes only to Ino.Failure, which logs its type.
    [Fact]
    public void CatchBodiesNeverUseTheExceptionText()
    {
        var uses = new List<string>();
        foreach (var file in Scripts())
        {
            var text = File.ReadAllText(file);
            foreach (Match caught in Catch().Matches(text))
            {
                var name = caught.Groups["name"].Value;
                var body = Body(text, caught.Index + caught.Length);
                if (Regex.IsMatch(body, $@"\b{name}\s*\.\s*(Message|InnerException|ToString\s*\()|\{{\s*{name}\b"))
                { uses.Add($"{Path.GetFileName(file)}: catch ({caught.Groups["type"].Value} {name})"); }
            }
        }
        Assert.Empty(uses);
    }

    // The catch's block: from its first '{' to the matching '}'.
    private static string Body(string text, int from)
    {
        var start = text.IndexOf('{', from);
        if (start < 0) { return ""; }
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') { depth++; }
            else if (text[i] == '}' && --depth == 0) { return text[start..(i + 1)]; }
        }
        return text[start..];
    }

    private static IEnumerable<string> Scripts() => Directory.EnumerateFiles(Sources, "*.cs");

    // error.Message (or any caught exception's Message), and the router/agent error strings that carry it.
    [GeneratedRegex(@"\b(error|ex|exception)\.Message\b|\b(asked|routed|outcome|retained)\.Error\b")]
    private static partial Regex Echo();

    [GeneratedRegex(@"(\bStatus\(brain,|\bstatus\.(Try)?Set\(|\bDetail\s*=)(?<arg>[^;]*)")]
    private static partial Regex StatusWrite();

    // A non-empty string literal or any interpolation ("" clears the line and is allowed).
    [GeneratedRegex(@"\$""|@""|(?<!"")""(?!"")")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\bcatch\s*\(\s*(?<type>[\w.]+)\s+(?<name>\w+)\s*\)")]
    private static partial Regex Catch();

    [GeneratedRegex(@"Get<IForm>|\bFormState\b|\bFormFieldState\b|\bFormSecret\b")]
    private static partial Regex FormRead();
}
