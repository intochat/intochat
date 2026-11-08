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

    // Deploys alias's shipped files; the same files deploy once (the studio's step key), so a repeat is answered as landed. A
    // waiting install's plan is approved through approve, and the deploy is read again until it has landed (a bounded wait).
    public static async Task<UnitDeployResult> Deploy(ISynapseStudio studio, string alias, Func<string, Task> approve)
    {
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(approve);
        var files = Files(alias);
        if (files.Count == 0) { throw new ArgumentException($"The OS ships no unit {alias}.", nameof(alias)); }
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = await studio.DeployFiles(alias, files, none);
        for (var attempt = 0; result.Ok && Waiting(result) is { } plan && attempt < 50; attempt++)
        {
            if (attempt == 0) { await approve(plan); }
            else { await Task.Delay(TimeSpan.FromMilliseconds(100)); }
            result = await studio.DeployFiles(alias, files, none);
        }
        return result;
    }

    private static string? Waiting(UnitDeployResult result)
        => result.Installs.Values.FirstOrDefault(install => install.Status == DigitalBrain.InstallStatus.Pending)?.Reason;
}
