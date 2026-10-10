namespace DigitalBrain.Apps;

public interface IScriptSandbox
{
    bool CanRun { get; }
    string AuthoringDescription { get; }
    Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken);
    // Compiles and stores every file; Hashes maps each path to its stored assembly when Success.
    Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken);
    // Runs every ILogic<T> among the bundle's shared classes whose T matches the signal; each verdict
    // names its rule. A sandbox that cannot run rules has none to run.
    Task<Verdict[]> Judge(IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
        => Task.FromResult<Verdict[]>([]);
}

public sealed record ScriptContractModule(string Id);
public sealed record ScriptContractCatalog(IReadOnlyList<ScriptContractModule> Modules, IReadOnlyList<string> Contracts, string Example, bool Truncated = false);
public sealed record ScriptCompilationDiagnostic(string File, string Id, int Line, string Message);

public sealed record ScriptCompilationCheck(bool Success, IReadOnlyList<ScriptCompilationDiagnostic> Errors)
{
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
