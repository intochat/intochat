using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Runtime.Orleans;

/// <summary>Pure checks of topology configuration. Every issue names keys, never configured values.</summary>
public static class TopologyValidator
{
    /// <summary>The deprecated spelling of local+cloud, accepted with a warning (03-CONTEXT R4-09).</summary>
    public const string LocalCloudAlias = "local-cloud";

    private const string Allowed = "local, local+cloud or cloud";

    /// <summary>Parses a topology value; <c>local+cloud</c> is one value and is never split.</summary>
    /// <param name="value">The configured value.</param>
    /// <param name="topology">The parsed topology.</param>
    /// <param name="isAlias">True when the deprecated <c>local-cloud</c> spelling was used.</param>
    public static bool TryParse(string? value, out Topology topology, out bool isAlias)
    {
        isAlias = false;
        topology = Topology.Local;
        var trimmed = value?.Trim();
        foreach (var candidate in Enum.GetValues<Topology>())
        {
            if (string.Equals(trimmed, candidate.ToConfigValue(), StringComparison.OrdinalIgnoreCase))
            {
                topology = candidate;
                return true;
            }
        }
        if (string.Equals(trimmed, LocalCloudAlias, StringComparison.OrdinalIgnoreCase))
        {
            topology = Topology.LocalCloud;
            isAlias = true;
            return true;
        }
        return false;
    }

    /// <summary>Validates topology, cloud and retired keys. Reads configuration only.</summary>
    public static IReadOnlyList<TopologyIssue> Validate(IConfiguration configuration, IReadOnlyList<RetiredSetting> retired)
        => Evaluate(configuration, retired).Issues;

    internal static (Topology? Topology, IReadOnlyList<TopologyIssue> Issues, IReadOnlyList<string> Warnings) Evaluate(
        IConfiguration configuration, IReadOnlyList<RetiredSetting> retired)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(retired);
        var issues = new List<TopologyIssue>();
        var warnings = new List<string>();

        var retiredPresent = retired.Where(setting => configuration[setting.Key] is not null).ToList();
        issues.AddRange(retiredPresent.Select(setting =>
            new TopologyIssue(setting.Key, $"is retired and not read; use {setting.Replacement}.")));

        var hasCloudKeys = configuration.AsEnumerable()
            .Any(pair => pair.Value is not null && pair.Key.StartsWith(IntoChatOSNames.CloudSectionPrefix, StringComparison.OrdinalIgnoreCase));

        Topology topology;
        var raw = configuration[IntoChatOSNames.TopologyKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (hasCloudKeys || retiredPresent.Count > 0)
            {
                issues.Add(new TopologyIssue(IntoChatOSNames.TopologyKey,
                    $"set {IntoChatOSNames.TopologyKey} to {Allowed}; cloud or retired settings are present."));
                return (null, issues, warnings);
            }
            topology = Topology.Local;
        }
        else if (TryParse(raw, out var parsed, out var isAlias))
        {
            topology = parsed;
            if (isAlias)
            {
                warnings.Add($"{IntoChatOSNames.TopologyKey} uses the deprecated spelling {LocalCloudAlias}; write local+cloud.");
            }
        }
        else
        {
            issues.Add(new TopologyIssue(IntoChatOSNames.TopologyKey, $"must be one of {Allowed}."));
            return (null, issues, warnings);
        }

        foreach (var key in new[] { IntoChatOSNames.CloudStorageKey, IntoChatOSNames.CloudKeyVaultKey })
        {
            var set = configuration[key] is not null;
            if (set && topology != Topology.Cloud)
            {
                issues.Add(new TopologyIssue(key, "requires Topology=cloud."));
            }
            else if (!set && topology == Topology.Cloud)
            {
                issues.Add(new TopologyIssue(key, "is required when Topology=cloud."));
            }
        }

        var hasServer = configuration[IntoChatOSNames.CloudServerAddressKey] is not null;
        if (hasServer && topology != Topology.LocalCloud)
        {
            issues.Add(new TopologyIssue(IntoChatOSNames.CloudServerAddressKey, "requires Topology=local+cloud."));
        }
        else if (!hasServer && topology == Topology.LocalCloud)
        {
            issues.Add(new TopologyIssue(IntoChatOSNames.CloudServerAddressKey, "is required when Topology=local+cloud."));
        }

        return (topology, issues, warnings);
    }

    /// <summary>Throws one exception listing every issue; returns when there are none.</summary>
    /// <exception cref="InvalidOperationException">At least one issue.</exception>
    public static void ThrowIfInvalid(IEnumerable<TopologyIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var list = issues.ToList();
        if (list.Count == 0) { return; }
        throw new InvalidOperationException("The host will not start: " + string.Join("; ", list.Select(issue => issue.ToString())));
    }
}
