using IntoChat.OS.Modules.Apps.Debugger;

namespace IntoChat.OS.Modules.Apps;

// The deploy gate as other modules reach it: the Builder's own check (the host's compiler, then the scenarios on fake slots), the
// rule that keeps a changed unit's earlier scenarios, and the transcript of a scenario run.
internal sealed class UnitVerifier(IServiceProvider? services = null) : IUnitVerifier
{
    public int MaxSidecarChars => BuilderTools.MaxSidecarChars;

    public Task<UnitCheck> Verify(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyList<Scenario> scenarios, CancellationToken ct = default)
        => BuilderTools.VerifyPackageAsync(services, alias, files, scenarios, ct);

    public IReadOnlyList<Scenario> Keep(IReadOnlyDictionary<string, string>? existing, IReadOnlyList<Scenario> authored, string change)
        => KeptScenarios.Merge(existing, authored, change);

    public Task<string> Transcript(string alias, IReadOnlyDictionary<string, string> files, string? scenario, CancellationToken ct)
        => ScenarioTranscripts.Run(alias, files, scenario, ct);
}
