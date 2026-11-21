using System.Text.Json;
using System.Xml.Linq;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Tests.Support;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

internal sealed record DeploymentSnapshot(
    string[] Modules,
    string[] Resources,
    SortedDictionary<string, string?> Settings)
{
    static DeploymentSnapshot() => IntoChatOSHostingExtensions.InitializeConfigurationRoot();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static DeploymentSnapshot Of(IntoChatOSBuilder os)
    {
        ArgumentNullException.ThrowIfNull(os);

        var modules = os.Modules
            .Order(StringComparer.Ordinal)
            .ToArray();

        var resources = os.ApplicationBuilder.Resources
            .Select(static r => r.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var settings = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in os.Settings)
        {
            settings[key] = value;
        }

        return new DeploymentSnapshot(modules, resources, settings);
    }

    public DeploymentSnapshot Without(params string[] moduleIds)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        if (moduleIds.Length == 0)
        {
            return this;
        }

        var excluded = new HashSet<string>(moduleIds, StringComparer.OrdinalIgnoreCase);
        var prefixes = moduleIds
            .Select(static id => ModuleConfiguration.ModuleSection(id) + ":")
            .ToArray();

        var modules = Modules
            .Where(m => !excluded.Contains(m))
            .ToArray();

        var resources = Resources
            .Where(r => !excluded.Any(id =>
                string.Equals(r, $"Modules-{id}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var settings = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Settings)
        {
            if (prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            settings[key] = value;
        }

        return new DeploymentSnapshot(modules, resources, settings);
    }

    public bool Equals(DeploymentSnapshot? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!Modules.SequenceEqual(other.Modules, StringComparer.Ordinal)
            || !Resources.SequenceEqual(other.Resources, StringComparer.Ordinal)
            || Settings.Count != other.Settings.Count)
        {
            return false;
        }

        foreach (var (key, value) in Settings)
        {
            if (!other.Settings.TryGetValue(key, out var otherValue)
                || !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var m in Modules) { hash.Add(m, StringComparer.Ordinal); }
        foreach (var r in Resources) { hash.Add(r, StringComparer.Ordinal); }
        foreach (var (k, v) in Settings)
        {
            hash.Add(k, StringComparer.OrdinalIgnoreCase);
            hash.Add(v, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    public static DeploymentSnapshot Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AspireHosting", "Snapshots", fileName);
        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<DeploymentSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Snapshot '{path}' deserialized to null.");
        var normalized = new SortedDictionary<string, string?>(snapshot.Settings, StringComparer.OrdinalIgnoreCase);
        return snapshot with { Settings = normalized };
    }

    public static void SaveSource(string fileName, DeploymentSnapshot snapshot)
    {
        var unitTestDir = Path.GetDirectoryName(ProjectPath("IntoChat.OS.Tests.Unit"))!;
        var sourcePath = Path.Combine(unitTestDir, "AspireHosting", "Snapshots", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine;
        File.WriteAllText(sourcePath, json);

        var outputPath = Path.Combine(AppContext.BaseDirectory, "AspireHosting", "Snapshots", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
    }

    public static string ProjectPath(string projectName)
    {
        var root = TestHome.RepoRoot();
        var slnx = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx"));
        foreach (var project in slnx.Descendants("Project"))
        {
            var rel = project.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(rel))
            {
                continue;
            }

            if (string.Equals(Path.GetFileNameWithoutExtension(rel), projectName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(Path.Combine(root, rel));
            }
        }

        throw new FileNotFoundException($"Project '{projectName}' was not found in Intochat.OS.slnx.");
    }
}
