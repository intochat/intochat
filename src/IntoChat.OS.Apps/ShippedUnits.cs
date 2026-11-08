using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps;

// The unit sources the OS ships (src/IntoChat.OS.Apps/Units/<Unit>): embedded as files, deployed through the synapse studio like
// any authored unit. A shipped unit asks for no slots; its reactions and view are still needs the host asks about, and the OS
// answers that ask for its own source, as it installs its default apps (OsDefaults), so the person is not asked.
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

    // Deploys alias's shipped files; the same files deploy once (the studio's step key), so a repeat is answered as landed. Only
    // the ask this deploy raised for alias itself (its own install outcome, matched against the brain's pending installs by plan
    // and alias) is approved, through approve, after activity names the unit and what it asks for; any other pending install in
    // the brain, an authored unit's, is never touched. The deploy is then read again until it has landed (a bounded wait).
    //
    // Who approves: the framework's confirmations accept an approval only from a person (a direct, authenticated user call); a
    // Platform caller's approval is recorded as a denial (ConfirmationsNeuron.Resolve, SelfApproval). So a shipped unit is
    // approved as the brain's creator in the creator's own turn, the precedent OsDefaults follows for default apps. A platform
    // consent for shipped sources needs a DigitalBrain change (fix wave).
    public static async Task<UnitDeployResult> Deploy(ISynapseStudio studio, string alias, Func<string, Task<IReadOnlyList<DigitalBrain.PendingInstall>>> pending,
        Func<string, Task> approve, Action<string> activity)
    {
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(approve);
        ArgumentNullException.ThrowIfNull(activity);
        var files = Files(alias);
        if (files.Count == 0) { throw new ArgumentException($"The OS ships no unit {alias}.", nameof(alias)); }
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = await studio.DeployFiles(alias, files, none);
        var approved = false;
        for (var attempt = 0; result.Ok && Waiting(result, alias) is { } own && attempt < 50; attempt++)
        {
            if (!approved)
            {
                // The plan must be this alias's waiting install in the brain, not merely some pending plan.
                if ((await pending(alias)).FirstOrDefault(ask => ask.PlanId == own.Reason && ask.Alias == alias) is not { } ask) { return result; }
                activity($"Approving the shipped unit {alias}: {ask.Sentence}");
                await approve(ask.PlanId);
                approved = true;
            }
            else { await Task.Delay(TimeSpan.FromMilliseconds(100)); }
            result = await studio.DeployFiles(alias, files, none);
        }
        return result;
    }

    private static DigitalBrain.InstallOutcome? Waiting(UnitDeployResult result, string alias)
        => result.Installs.TryGetValue(alias, out var own) && own.Status == DigitalBrain.InstallStatus.Pending && own.Reason is { Length: > 0 } ? own : null;
}
