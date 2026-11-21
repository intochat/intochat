using System.Diagnostics;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

/// <summary>
/// Optional helper that launches the native Flutter desktop shell pointed at a running
/// <see cref="OsAppHostFixture"/> when <c>INTOCHAT_TEST_VISUAL=desktop</c> is set.
/// No-op when unset.
/// </summary>
public sealed class VisualDebugWindow : IAsyncDisposable
{
    private const string EnvVar = "INTOCHAT_TEST_VISUAL";
    private const string DesktopMode = "desktop";

    private readonly Process? _process;

    private VisualDebugWindow(Process? process) => _process = process;

    public bool IsRunning => _process is { HasExited: false };

    public static VisualDebugWindow LaunchIfEnabled(OsAppHostFixture host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!string.Equals(Environment.GetEnvironmentVariable(EnvVar), DesktopMode, StringComparison.OrdinalIgnoreCase))
        {
            return new VisualDebugWindow(process: null);
        }

        var binary = LocateFlutterBinary();
        if (binary is null)
        {
            return new VisualDebugWindow(process: null);
        }

        var kernelUrl = host.Os.BaseAddress?.ToString().TrimEnd('/')
            ?? throw new InvalidOperationException("OsAppHostFixture.Os.BaseAddress is not set.");

        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
        };
        startInfo.Environment["INTOCHATOS_UI_BASE"] = kernelUrl;
        if (!string.IsNullOrWhiteSpace(host.PresenceToken))
        {
            startInfo.ArgumentList.Add($"--presence-token={host.PresenceToken}");
        }

        var process = Process.Start(startInfo);
        return new VisualDebugWindow(process);
    }

    public ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                _process.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string? LocateFlutterBinary()
    {
        var root = OsAppHostFixture.RepoRoot();
        var binaryName = OperatingSystem.IsWindows() ? "intochat_os_flutter.exe" : "intochat_os_flutter";

        string[] candidates = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(root, "ui", "build", "windows", "x64", "runner", "Release", binaryName),
                Path.Combine(root, "ui", "build", "windows", "x64", "runner", "Debug", binaryName),
                Path.Combine(root, "artifacts", "ui", "windows", binaryName),
            ]
            : OperatingSystem.IsMacOS()
                ?
                [
                    Path.Combine(root, "ui", "build", "macos", "Build", "Products", "Release", "intochat_os_flutter.app", "Contents", "MacOS", binaryName),
                    Path.Combine(root, "ui", "build", "macos", "Build", "Products", "Debug", "intochat_os_flutter.app", "Contents", "MacOS", binaryName),
                ]
                :
                [
                    Path.Combine(root, "ui", "build", "linux", "x64", "release", "bundle", binaryName),
                    Path.Combine(root, "ui", "build", "linux", "x64", "debug", "bundle", binaryName),
                ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
