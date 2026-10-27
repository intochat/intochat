using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;

namespace IntoChat.OS.Testing;

// Mirrors the .NET environment configuration provider at the harness boundary, retaining values
// so composition facts can inspect resource references as well as resolved strings.
public static class EnvironmentProjection
{
    public static Dictionary<string, T> Configuration<T>(IEnumerable<KeyValuePair<string, T>> environment)
        => environment.ToDictionary(pair => IntoChatOSConfiguration.ConfigurationName(pair.Key),
            pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
