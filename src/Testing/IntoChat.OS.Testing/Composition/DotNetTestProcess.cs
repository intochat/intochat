using System.Diagnostics;

namespace IntoChat.OS.Testing;

// Child builds against disposable test feeds must release loaded tasks when they exit,
// so the fixture can remove its package cache without changing the developer's build servers.
public static class DotNetTestProcess
{
    public static void DisableBuildServerReuse(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
    }
}
