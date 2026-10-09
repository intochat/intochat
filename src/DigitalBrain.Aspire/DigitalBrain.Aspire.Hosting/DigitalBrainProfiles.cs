using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace DigitalBrain.Aspire.Hosting;

// A profile is a named options preset: profiles/{name}.json beside the app host, composed left to right
// with '+' ("local+browser"). Options only — credentials stay Integrations.
public static class DigitalBrainProfiles
{
    public const string ProfileKey = "DigitalBrain:Profile";
    public const string DefaultProfile = "dev";
    public const string Directory = "profiles";

    public static IReadOnlyList<string> AddDigitalBrainProfiles(this IConfigurationBuilder configuration, string appHostDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        var root = Path.Combine(appHostDirectory, Directory);
        var names = Parse(configuration.Build()[ProfileKey] ?? DefaultProfile);
        // Profiles sit above the shipped appsettings and below environment and command line, so an
        // explicit setting still wins over the preset it was launched with.
        var at = configuration.Sources.ToList().FindLastIndex(source => source is JsonConfigurationSource) + 1;
        foreach (var name in names)
        {
            var path = Path.Combine(root, name + ".json");
            if (!File.Exists(path))
            {
                var known = System.IO.Directory.Exists(root)
                    ? string.Join(", ", System.IO.Directory.GetFiles(root, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal))
                    : "none";
                throw new InvalidOperationException($"Unknown DigitalBrain profile '{name}'. Known profiles: {known}.");
            }
            var source = new JsonConfigurationSource { Path = path, Optional = false, ReloadOnChange = false };
            source.ResolveFileProvider();
            configuration.Sources.Insert(at++, source);
        }
        return names;
    }

    public static string[] Parse(string profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var names = profile.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 ? [DefaultProfile] : names;
    }
}
