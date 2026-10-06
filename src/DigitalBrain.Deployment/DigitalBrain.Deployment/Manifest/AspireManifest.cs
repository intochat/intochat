using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Framework;

namespace DigitalBrain.Deployment;

// The AppHost's deployment manifest (--publisher manifest): every parameter a module declared, every
// resource it runs, and the brain runtime's environment written as {resource.path} expressions.
public sealed partial class AspireManifest
{
    private static readonly string ModulesEnvironmentPrefix =
        DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ModulesSectionName) + "__";

    private readonly Dictionary<string, JsonElement> _resources;

    private AspireManifest(Dictionary<string, JsonElement> resources) => _resources = resources;

    public static AspireManifest Load(string path) => Parse(File.ReadAllText(path));

    public static AspireManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var resources = document.RootElement.GetProperty("resources").EnumerateObject()
            .ToDictionary(resource => resource.Name, resource => resource.Value.Clone(), StringComparer.Ordinal);
        return new AspireManifest(resources);
    }

    public IEnumerable<string> ResourceNames => _resources.Keys;

    public string TypeOf(string resource) => Resource(resource).GetProperty("type").GetString()!;

    public JsonElement Resource(string name) => _resources.TryGetValue(name, out var resource)
        ? resource
        : throw new InvalidOperationException($"The manifest has no resource '{name}'.");

    // The image a container resource runs, so the cloud runs exactly what the AppHost runs locally.
    public string Image(string resource) => Resource(resource).GetProperty("image").GetString()
        ?? throw new InvalidOperationException($"'{resource}' is not a container resource.");

    public bool IsSecretParameter(string name)
        => TypeOf(name) == "parameter.v0"
            && Resource(name).TryGetProperty("inputs", out var inputs)
            && inputs.TryGetProperty("value", out var value)
            && value.TryGetProperty("secret", out var secret) && secret.GetBoolean();

    public IEnumerable<string> Parameters => _resources.Keys.Where(name => TypeOf(name) == "parameter.v0");

    // The brain runtime is the project that loads modules; its environment carries the module list.
    public string Runtime => _resources.Where(resource => resource.Value.GetProperty("type").GetString() == "project.v0"
            && resource.Value.TryGetProperty("env", out var env)
            && env.EnumerateObject().Any(variable => variable.Name.StartsWith(ModulesEnvironmentPrefix, StringComparison.Ordinal)))
        .Select(resource => resource.Key).SingleOrDefault()
        ?? throw new InvalidOperationException($"The manifest has no brain runtime project (one whose env carries {ModulesEnvironmentPrefix} keys).");

    public IReadOnlyDictionary<string, string> Environment(string resource)
        => Resource(resource).TryGetProperty("env", out var env)
            ? env.EnumerateObject().ToDictionary(variable => variable.Name, variable => variable.Value.GetString() ?? "", StringComparer.Ordinal)
            : new Dictionary<string, string>();

    // The manifest resources a connection string reads, nearest first: ConnectionStrings__compute leads to
    // the database value, then the server container behind it.
    public IReadOnlyList<string> ResourcesBehindConnection(string runtime, string connectionName)
    {
        List<string> found = [];
        var pending = new Queue<string>();
        if (Environment(runtime).TryGetValue("ConnectionStrings__" + connectionName, out var expression)) { Enqueue(expression); }
        while (pending.TryDequeue(out var resource))
        {
            if (found.Contains(resource) || !_resources.ContainsKey(resource)) { continue; }
            found.Add(resource);
            if (Resource(resource).TryGetProperty("connectionString", out var connection)) { Enqueue(connection.GetString()!); }
        }
        return found;

        void Enqueue(string text)
        {
            foreach (Match match in Reference().Matches(text)) { pending.Enqueue(match.Groups[1].Value); }
        }
    }

    public string ServingContainer(string runtime, string connectionName)
        => ResourcesBehindConnection(runtime, connectionName).FirstOrDefault(resource => TypeOf(resource) == "container.v0")
            ?? throw new InvalidOperationException($"No container serves ConnectionStrings__{connectionName} in the manifest.");

    [GeneratedRegex(@"\{([A-Za-z0-9_-]+)\.")]
    private static partial Regex Reference();

    public IReadOnlyList<string> Modules(string runtime)
        => [.. Environment(runtime).Where(variable => variable.Key.StartsWith(ModulesEnvironmentPrefix, StringComparison.Ordinal))
            .OrderBy(variable => int.Parse(variable.Key[ModulesEnvironmentPrefix.Length..], System.Globalization.CultureInfo.InvariantCulture))
            .Select(variable => variable.Value)];

    // The composed module ids the runtime's environment already carries under the one grammar:
    // DigitalBrain__Modules__{id}__… keys. The numeric type-name array is legacy and is not an id.
    public IReadOnlyList<ModuleId> ComposedModuleIds(string runtime)
        => [.. Environment(runtime).Keys
            .Where(key => key.StartsWith(ModulesEnvironmentPrefix, StringComparison.Ordinal))
            .Select(key => key[ModulesEnvironmentPrefix.Length..].Split("__", 2)[0])
            .Where(id => id.Length > 0 && !int.TryParse(id, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ModuleId(id))];
}
