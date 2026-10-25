using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.ServiceDefaults;

// Refuses to start on configuration written for the previous product name. There is no compatibility
// layer: a legacy key that is silently ignored would start the process with defaults (a new master key,
// an empty store), so outside Development the process stops and names every offender and its replacement.
// Only names are reported, never values. Nothing on disk is deleted or moved.
public static class LegacyConfigurationGuard
{
    // The legacy tokens are assembled so this file carries no literal of the retired name.
    public const string IgnoreLegacyDataDirectoryKey = "IntoChatOS:Guard:IgnoreLegacyDataDirectory";

    private static readonly string Pascal = "Digital" + "Brain";
    private static readonly string Lower = "digital" + "brain";
    private static readonly string Upper = "DIGITAL" + "BRAIN";

    // Settings this release retired (D-11, D-13). Each fails startup in every environment naming its replacement;
    // unlike the product rename these are not Development warnings, because a silently ignored one moves local state.
    public static readonly IReadOnlyList<(string Name, string Replacement)> RetiredSettings =
    [
        ("IntoChatOS:Hosting:PersistentStorage", IntoChat.OS.Framework.IntoChatOSNames.HomeKey),
        ("IntoChatOS:Platform:Storage:Directory", IntoChat.OS.Framework.IntoChatOSNames.HomeKey),
    ];

    public static void Verify(IConfiguration configuration, IHostEnvironment environment, ILogger? logger = null)
        => Verify(configuration, environment, EnvironmentVariableNames(), LocalApplicationData(), logger);

    public static void Verify(
        IConfiguration configuration,
        IHostEnvironment environment,
        IEnumerable<string> environmentVariableNames,
        string? localApplicationData,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(environmentVariableNames);
        var names = environmentVariableNames as IReadOnlyCollection<string> ?? environmentVariableNames.ToArray();
        var retired = FindRetired(configuration, names);
        if (retired.Count > 0)
        {
            throw new InvalidOperationException("Retired settings are not read: "
                + string.Join("; ", retired.Select(offender => $"{offender.Name} -> {offender.Replacement}"))
                + ". Local state lives under IntoChatOS:Home (<Home>/state); remove each setting, then start again.");
        }
        // The pre-Home state directory is not migrated (clean break): one note so an operator knows where state went.
        if (localApplicationData is { Length: > 0 } && Directory.Exists(Path.Combine(localApplicationData, "IntoChatOS", "runtime")))
        {
            logger?.LogInformation("{Directory} is from an earlier release and is not read or migrated; local state now lives in <{HomeKey}>/state.",
                Path.Combine("%LOCALAPPDATA%", "IntoChatOS", "runtime"), IntoChat.OS.Framework.IntoChatOSNames.HomeKey);
        }
        var offenders = Find(configuration, names, localApplicationData);
        if (offenders.Count == 0) { return; }
        var message = "Configuration written for the previous product name is not read (no compatibility layer; see release notes 0.3.0): "
            + string.Join("; ", offenders.Select(offender => $"{offender.Name} -> {offender.Replacement}"))
            + ". Rename each setting, then start again.";
        if (environment.IsDevelopment())
        {
            logger?.LogWarning("{Message}", message);
            return;
        }
        throw new InvalidOperationException(message);
    }

    public static IReadOnlyList<(string Name, string Replacement)> Find(
        IConfiguration configuration,
        IEnumerable<string> environmentVariableNames,
        string? localApplicationData)
    {
        var found = new List<(string Name, string Replacement)>();
        foreach (var pair in configuration.AsEnumerable())
        {
            var key = pair.Key;
            if (key.StartsWith(Pascal + ":", StringComparison.OrdinalIgnoreCase))
            { found.Add((key, "IntoChatOS:" + key[(Pascal.Length + 1)..])); }
            else if (key.StartsWith("Parameters:", StringComparison.OrdinalIgnoreCase) && key.Contains(Lower + "-", StringComparison.OrdinalIgnoreCase))
            { found.Add((key, key.Replace(Lower + "-", "intochat-os-", StringComparison.OrdinalIgnoreCase))); }
        }
        foreach (var name in environmentVariableNames)
        {
            if (name.StartsWith(Pascal + "__", StringComparison.OrdinalIgnoreCase))
            { found.Add((name, "IntoChatOS__" + name[(Pascal.Length + 2)..])); }
            else if (name.StartsWith(Upper + "_", StringComparison.Ordinal))
            { found.Add((name, "INTOCHATOS_" + name[(Upper.Length + 1)..])); }
        }
        // The directory is machine state, not configuration: automated test hosts on a developer machine that
        // still has the old directory set this key (Testing module initializer); operators never should.
        if (localApplicationData is { Length: > 0 }
            && !configuration.GetValue(IgnoreLegacyDataDirectoryKey, false)
            && Directory.Exists(Path.Combine(localApplicationData, Pascal))
            && !Directory.Exists(Path.Combine(localApplicationData, "IntoChatOS")))
        { found.Add((Path.Combine("%LOCALAPPDATA%", Pascal), Path.Combine("%LOCALAPPDATA%", "IntoChatOS"))); }
        return found.DistinctBy(offender => offender.Name, StringComparer.Ordinal).ToArray();
    }

    // Configuration keys and environment variable names (':' or '__') of the retired settings that are set; names only.
    public static IReadOnlyList<(string Name, string Replacement)> FindRetired(IConfiguration configuration, IEnumerable<string> environmentVariableNames)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environmentVariableNames);
        var found = new List<(string Name, string Replacement)>();
        foreach (var (name, replacement) in RetiredSettings)
        {
            if (configuration[name] is not null) { found.Add((name, replacement)); }
            var variable = name.Replace(":", "__", StringComparison.Ordinal);
            if (environmentVariableNames.Any(candidate => string.Equals(candidate, variable, StringComparison.OrdinalIgnoreCase)))
            { found.Add((variable, replacement.Replace(":", "__", StringComparison.Ordinal))); }
        }
        return found.DistinctBy(offender => offender.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnvironmentVariableNames()
        => Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(key => key.ToString()!).ToArray();

    private static string? LocalApplicationData()
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } path ? path : null;
}
