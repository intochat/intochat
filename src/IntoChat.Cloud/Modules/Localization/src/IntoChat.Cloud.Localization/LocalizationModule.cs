using System.Text.Json;
using DigitalBrain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.Cloud.Localization;

public interface ILocalizer
{
    string Get(string language, string key);

    IReadOnlyCollection<string> Languages { get; }
}

// String tables are data. There is no generator: lobby and account screens look keys up at runtime.
[Module(Id)]
public sealed class LocalizationModule : Module
{
    public const string Id = "localization";

    public static bool LoadsUnder(string? topology) => string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ILocalizer, TableLocalizer>();
    }
}

public sealed class TableLocalizer : ILocalizer
{
    private readonly Dictionary<string, Dictionary<string, string>> tables;

    public TableLocalizer()
    {
        tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(TableLocalizer).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var language = LanguageOf(name);
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing string table {name}.");
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            tables[language] = new Dictionary<string, string>(entries, StringComparer.Ordinal);
        }
    }

    public IReadOnlyCollection<string> Languages => tables.Keys;

    public string Get(string language, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (tables.TryGetValue(language ?? "", out var table) && table.TryGetValue(key, out var value)) { return value; }
        if (tables.TryGetValue("en", out var english) && english.TryGetValue(key, out var fallback)) { return fallback; }
        return key;
    }

    private static string LanguageOf(string resourceName)
    {
        var file = resourceName.Split('.').Reverse().Skip(1).First();
        return file;
    }
}
