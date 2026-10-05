using System.Text.Json;
using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

// Save = commit: the live app's content snapshots into its package, red allowed — commits
// were never gated. Publish stays the only green gate, and a scenario run commits first so
// every verified state is a reproducible revision.
internal sealed class LiveAppPackaging(IDigitalBrain brain)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PackageRevisionRef> Save(IApp app, PackageId id, string message)
    {
        var content = Snapshot(await app.ReadContent());
        var package = brain.Get<IPackage>(id.ToString());
        var head = (await package.Read()).Head;
        if (head is not null && Canonical((await package.ReadRevision(head)).Content) == Canonical(content))
        { return new(id, head); }
        var revision = await package.Commit(new(Guid.NewGuid(), head, content, message));
        return new(id, revision.Id);
    }

    public async Task<AppTestRun> RunScenarios(IApp app, PackageId id, string[]? scenarioIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = await Save(app, id, "Scenario run");
        var verification = await brain.Get<IAppVerification>(IAppVerification.Key(reference)).Verify();
        var verdicts = scenarioIds is { Length: > 0 }
            ? verification.Run.Scenarios.Where(verdict => verdict.ScenarioId is { } sid && scenarioIds.Contains(sid, StringComparer.Ordinal)).ToArray()
            : verification.Run.Scenarios;
        if (verdicts.Length > 0) { await app.RecordVerdicts(verdicts, Guid.NewGuid()); }
        return verification.Run;
    }

    private static PackageContent Snapshot(AppContent content)
    {
        var files = new Dictionary<string, string>(content.Files, StringComparer.Ordinal);
        if (content.Document is { } document)
        {
            files[AppDocumentCodec.Path] = AppDocumentCodec.Encode(document);
            files[PackageContent.SpecPath] = AppDocumentCodec.ExportSpec(document);
        }
        return new PackageContent(content.Manifest, files);
    }

    private static string Canonical(PackageContent content) => JsonSerializer.Serialize(new
    {
        content.Manifest,
        files = new SortedDictionary<string, string>(content.Files?.ToDictionary() ?? [], StringComparer.Ordinal),
    }, Json);
}
