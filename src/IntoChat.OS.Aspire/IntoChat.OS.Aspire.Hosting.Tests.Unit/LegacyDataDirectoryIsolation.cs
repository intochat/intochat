using System.Runtime.CompilerServices;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

// This test assembly builds server hosts without touching the shared Testing library first, so it opts out of
// the startup guard's machine-state directory check itself (see IntoChat.OS.Testing.LegacyDataDirectoryIsolation).
internal static class LegacyDataDirectoryIsolation
{
    [ModuleInitializer]
    internal static void Apply()
        => Environment.SetEnvironmentVariable(IntoChat.OS.ServiceDefaults.LegacyConfigurationGuard.IgnoreLegacyDataDirectoryKey.Replace(":", "__"), "true");
}
