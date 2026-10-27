using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Configuration;

namespace IntoChat.OS.Framework.Configuration;

public static class ModuleSettingsValidation
{
    // The test harness's reserved configuration section: host-owned, never a module's to set.
    public const string TestingSectionPrefix = "IntoChatOS:Testing:";

    public static void ValidatePublicSettings(IReadOnlyList<ModuleDefinition> modules)
    {
        foreach (var key in modules.SelectMany(m => m.Settings.Keys))
        {
            if (IsOptionsKey(key)) { continue; }
            if (string.IsNullOrWhiteSpace(key) || key.Contains("__", StringComparison.Ordinal)
                || key.StartsWith("Orleans:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(TestingSectionPrefix, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(IntoChatOSConfiguration.ModulesSectionName + ":", StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("Module configuration cannot override host-owned settings.", nameof(modules)); }
            if (IsCredentialName(key))
            { throw new ArgumentException("Credentials require private configuration transport.", nameof(modules)); }
        }
    }

    // A module's own options key (IntoChatOS:Modules:{name}:Options:...), which the module may set.
    internal static bool IsOptionsKey(string key)
        => key.Split(':') is [var root, var modules, var name, var options, ..]
            && root.Equals("IntoChatOS", StringComparison.OrdinalIgnoreCase)
            && modules.Equals("Modules", StringComparison.OrdinalIgnoreCase)
            && options.Equals("Options", StringComparison.OrdinalIgnoreCase)
            && name.Length > 0 && !name.Contains("__", StringComparison.Ordinal) && !int.TryParse(name, out _);

    // Public: the credential-name rule shared by settings validation and the architecture contract rules.
    public static bool IsCredentialName(string name) =>
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase) || name.EndsWith("PrivateKeyPem", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("AccessToken", StringComparison.OrdinalIgnoreCase) || name.EndsWith("RefreshToken", StringComparison.OrdinalIgnoreCase);
}
