using System.Runtime.CompilerServices;

namespace IntoChat.OS.Testing;

// Test hosts run as Production and may sit on a machine that still has the previous product's local data
// directory. The startup guard's directory check is machine state, so automated hosts opt out of that one
// check here; the key checks stay on, and operators never set this.
internal static class LegacyDataDirectoryIsolation
{
#pragma warning disable CA2255 // test-support library: runs when the test assembly first touches Testing, before any host is built
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Apply()
        => Environment.SetEnvironmentVariable("IntoChatOS__Guard__IgnoreLegacyDataDirectory", "true");
}
