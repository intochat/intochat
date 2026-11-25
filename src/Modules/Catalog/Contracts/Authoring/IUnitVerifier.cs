namespace IntoChat.OS.Modules.Apps;

// What verifying a unit found: compile diagnostics and scenario failures, one line each, together.
public sealed record UnitCheck(bool Passed, IReadOnlyList<ScriptCompilationDiagnostic> Diagnostics, IReadOnlyList<string> Failures);

// The deploy gate of a unit, for code that carries a unit's files but does not author them (a package import, an installer of a
// generated unit, the Creator app). A host service, not a neuron: a check compiles and runs scenarios off any neuron's turn.
public interface IUnitVerifier
{
    // The most characters a file of a unit that is not C# may hold.
    int MaxSidecarChars { get; }

    // Compiles the files as a deploy will, then runs every live scenario on fake slots. A unit that does not compile runs none.
    Task<UnitCheck> Verify(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyList<Scenario> scenarios, CancellationToken ct = default);

    // The scenarios a changed unit keeps: those of its current files (scenarios.json) and the new ones as one list. Throws
    // InvalidDataException when the kept file does not read or a new scenario supersedes one the unit does not have.
    IReadOnlyList<Scenario> Keep(IReadOnlyDictionary<string, string>? existing, IReadOnlyList<Scenario> authored, string change);

    // Runs the unit's kept scenarios (all, or the one named) and answers the transcript a person reads. The caller bounds the time.
    Task<string> Transcript(string alias, IReadOnlyDictionary<string, string> files, string? scenario, CancellationToken ct);
}
