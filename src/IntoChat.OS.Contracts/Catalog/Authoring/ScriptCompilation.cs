namespace IntoChat.OS.Modules.Apps;

// What compiling a unit's C# files found: the Builder's check_unit answer.
public sealed record ScriptCompilationDiagnostic(string File, string Id, int Line, string Message);

public sealed record ScriptCompilationCheck(bool Success, IReadOnlyList<ScriptCompilationDiagnostic> Errors)
{
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
