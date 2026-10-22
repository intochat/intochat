namespace IntoChat.OS.Apps;

public interface IScriptSandbox
{
    bool CanRun { get; }
    string AuthoringDescription { get; }
    Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken);
    Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken);
    // Compiles and stores every file; Hashes maps each path to its stored assembly when Success.
    Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken);
    // Runs every ILogic<T> among the bundle's shared classes whose T matches the signal, in the brain's
    // sandbox session; each verdict names its rule. Rules fail closed: a sandbox that cannot run them rejects.
    Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken);
}

public sealed record ScriptContractModule(string Id);
public sealed record ScriptContractCatalog(IReadOnlyList<ScriptContractModule> Modules, IReadOnlyList<string> Contracts, string Example, bool Truncated = false);
public sealed record ScriptCompilationDiagnostic(string File, string Id, int Line, string Message);

public sealed record ScriptCompilationCheck(bool Success, IReadOnlyList<ScriptCompilationDiagnostic> Errors)
{
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
