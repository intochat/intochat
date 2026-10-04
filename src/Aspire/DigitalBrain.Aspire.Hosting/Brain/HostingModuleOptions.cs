using System.Text.Json;
using DigitalBrain.Contracts;
using Microsoft.Extensions.Configuration;
namespace DigitalBrain.Aspire.Hosting;

public static class HostingModuleOptions
{
    public static T GetModuleOptions<T>(this IConfiguration configuration, string id) where T : class, IModuleOptions, new()
    {
        var value = configuration.GetSection($"DigitalBrain:Modules:{id}:Options").Get<T>() ?? new();
        value.Validate();
        return value;
    }
    internal static Dictionary<string, string?> Flatten<T>(T options, string id) where T : new()
    {
        ModuleOptionsShape.Validate<T>();
        // Only values the deployment actually decided become configuration: a leaf equal to
        // the type's default is omitted, so it cannot pin the silo's own configuration.
        var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Collect(JsonSerializer.SerializeToElement(new T()), $"DigitalBrain:Modules:{id}:Options", defaults);
        var declared = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Collect(JsonSerializer.SerializeToElement(options), $"DigitalBrain:Modules:{id}:Options", declared);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in declared)
        {
            if (!defaults.TryGetValue(pair.Key, out var fallback) || fallback != pair.Value) { values[pair.Key] = pair.Value; }
        }
        return values;
        static void Collect(JsonElement element, string path, Dictionary<string, string?> into)
        {
            if (element.ValueKind == JsonValueKind.Object)
            { foreach (var property in element.EnumerateObject()) { Collect(property.Value, path + ":" + property.Name, into); } }
            else if (element.ValueKind == JsonValueKind.Array)
            { var index = 0; foreach (var value in element.EnumerateArray()) { Collect(value, path + ":" + index++, into); } }
            else { into[path] = element.ValueKind == JsonValueKind.Null ? null : element.ToString(); }
        }
    }
}
