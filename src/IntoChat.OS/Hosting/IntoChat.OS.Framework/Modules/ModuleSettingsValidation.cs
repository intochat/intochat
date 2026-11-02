// os-only: os credential-name rule shared by settings validation and architecture rules.
namespace IntoChat.OS.Framework.Configuration;

public static class ModuleSettingsValidation
{
    // Public: the credential-name rule shared by settings validation and the architecture contract rules.
    public static bool IsCredentialName(string name) =>
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase) || name.EndsWith("PrivateKeyPem", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("AccessToken", StringComparison.OrdinalIgnoreCase) || name.EndsWith("RefreshToken", StringComparison.OrdinalIgnoreCase);
}
