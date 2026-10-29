using System.Reflection;
using System.Text.Json;

namespace IntoChat.OS.Modules.Apps;

// Module id, type name and capability ids. No secrets, accounts or tokens.
internal sealed record ModuleListing(string Id, string Name, string Capabilities);

internal static class InstalledModules
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ModuleListing[] List()
    {
        var rows = new List<ModuleListing>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = [.. error.Types.Where(type => type is not null)!]; }
            catch (Exception error) when (error is not OperationCanceledException) { continue; }
            foreach (var type in types)
            {
                if (type is not { IsClass: true, IsAbstract: false } || type.ContainsGenericParameters) { continue; }
                if (type.GetCustomAttribute<DigitalBrain.ModuleAttribute>(inherit: false) is null) { continue; }
                if (!typeof(DigitalBrain.Module).IsAssignableFrom(type)) { continue; }
                DigitalBrain.ModuleManifest manifest;
                try { manifest = DigitalBrain.ModuleManifest.Of(type); }
                catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { continue; }
                var capabilities = string.Join(", ", manifest.Provides.Select(id => id.ToString()).Order(StringComparer.Ordinal));
                rows.Add(new(manifest.Id ?? type.Name, type.Name, capabilities));
            }
        }
        return [.. rows.OrderBy(row => row.Id, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal)];
    }

    internal static string Json() => JsonSerializer.Serialize(List(), JsonOptions);
}
