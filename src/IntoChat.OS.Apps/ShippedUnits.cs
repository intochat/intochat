using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps;

// The unit sources the OS ships (src/IntoChat.OS.Apps/Units/<Unit>): embedded as files, deployed through the synapse studio like
// any authored unit, with a headless app record whose activity says what the install waits on. A shipped unit asks for no slots;
// its reactions and view are needs the person consents to, through the normal approvals: the OS never approves them.
public static class ShippedUnits
{
    private const string Prefix = "IntoChat.OS.Apps.Units.";

    // A shipped unit's files keyed by path: its .cs source, spec.md and scenarios.json. Empty for an alias the OS does not ship.
    public static IReadOnlyDictionary<string, string> Files(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var assembly = typeof(ShippedUnits).Assembly;
        var prefix = Prefix + alias + "/";
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not embedded.");
            using var reader = new StreamReader(stream);
            files[name[prefix.Length..]] = reader.ReadToEnd();
        }
        return files;
    }

    // Deploys alias's shipped files and leaves the install as the host answers it, Pending until the person approves. The first
    // deploy records the unit as a headless app and begins its build. While the record is Building (waiting for approval, or a
    // crash between Create and the end of the build), every deploy ends the build by the install the studio reports: Pending keeps
    // it waiting, installed (Unchanged once the person approved) makes it Live. So the connect after an approval finishes the build,
    // and a connect after a crash deploys again (the studio deploys anew when the installs neuron knows nothing of the alias).
    // A Live (or otherwise settled) record only deploys again: the same files deploy once.
    public static async Task<UnitDeployResult> Deploy(ISynapseStudio studio, IApp app, string alias, string title)
    {
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(app);
        var files = Files(alias);
        if (files.Count == 0) { throw new ArgumentException($"The OS ships no unit {alias}.", nameof(alias)); }
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        var record = await app.Read();
        if (record.Manifest.Title.Length == 0)
        {
            await app.Create(new AppManifest(title, files.TryGetValue("spec.md", out var spec) ? FirstLine(spec) : title, [], [], [], Headless: true), spec ?? "");
            await app.BeginBuild("shipped unit " + alias);
        }
        // Drafted: a crash between Create and BeginBuild; its build begins now.
        else if (record.Status == AppStatus.Drafted) { await app.BeginBuild("shipped unit " + alias); }
        else if (record.Status != AppStatus.Building) { return await studio.DeployFiles(alias, files, none); }
        var attempt = await app.CurrentBuildAttempt();
        var result = await studio.DeployFiles(alias, files, none);
        if (!result.Ok) { await app.CompleteBuild(attempt, null, result.Text); return result; }
        var install = result.Installs.TryGetValue(alias, out var own) ? own : new DigitalBrain.InstallOutcome(DigitalBrain.InstallStatus.Unchanged, "", null);
        if (install.Status is DigitalBrain.InstallStatus.Refused or DigitalBrain.InstallStatus.Denied)
        { await app.CompleteBuild(attempt, null, $"{alias} was not installed: {install.Reason ?? install.Status.ToString()}"); }
        else { await app.CompleteUnitBuild(attempt, result.Hash ?? "", install.Status, install.Sentence); }
        return result;
    }

    private static string FirstLine(string text)
        => text.Split('\n').Select(line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
}
