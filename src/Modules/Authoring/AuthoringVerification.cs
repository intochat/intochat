using System.Text;

namespace IntoChat.OS.Modules.Authoring;

/// <summary>
/// A diagnostic message from an authoring compilation or verification pass.
/// </summary>
public sealed record AuthoringDiagnostic(string File, string Id, int Line, string Message);

/// <summary>
/// Domain verification outcome for an authored unit candidate.
/// </summary>
public sealed record AuthoringVerification(
    bool Passed,
    IReadOnlyList<AuthoringDiagnostic> Diagnostics,
    IReadOnlyList<string> Failures)
{
    public static AuthoringVerification Success(IReadOnlyList<AuthoringDiagnostic>? diagnostics = null)
        => new(true, diagnostics ?? [], []);

    public static AuthoringVerification Failure(IReadOnlyList<AuthoringDiagnostic> diagnostics, IReadOnlyList<string> failures)
        => new(false, diagnostics, failures);

    public string FormatReport()
    {
        if (Passed && Diagnostics.Count == 0 && Failures.Count == 0)
        {
            return "Verification passed successfully.";
        }

        var sb = new StringBuilder(Passed ? "Verification passed with warnings:" : "Verification failed:");
        foreach (var diagnostic in Diagnostics)
        {
            sb.Append('\n').Append($"[{diagnostic.Id}] {diagnostic.File}({diagnostic.Line}): {diagnostic.Message}");
        }
        foreach (var failure in Failures)
        {
            sb.Append('\n').Append($"[Failure] {failure}");
        }
        return sb.ToString();
    }
}
