namespace IntoChat.OS.Modules.Microsoft.Roslyn;

// C# sources reviewed by syntax alone, with no workspace: each Path is only the label findings carry.
public sealed record SyntaxReview(IReadOnlyList<SyntaxSource> Sources);

public sealed record SyntaxSource(string Path, string Text);

public enum SyntaxFindingKind
{
    Analyzer,
    ArchitectureFact,
}

public sealed record SyntaxFinding(
    string RuleId,
    SyntaxFindingKind Kind,
    string Path,
    int Line,
    bool Blocking,
    string Message);

public sealed record SyntaxReviewResult(IReadOnlyList<SyntaxFinding> Findings);
