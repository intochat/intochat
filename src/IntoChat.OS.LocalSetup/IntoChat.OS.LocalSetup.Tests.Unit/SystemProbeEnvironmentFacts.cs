namespace IntoChat.OS.LocalSetup.Tests.Unit;

public sealed class SystemProbeEnvironmentFacts
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(23, true)]
    public async Task FlutterLauncherOnPathFindsItsOwnFilesAndPreservesProcessResults(int exitCode, bool quotedPath)
    {
        var ct = TestContext.Current.CancellationToken;
        using var home = TestHome.Create();
        var tools = Directory.CreateDirectory(Path.Combine(home.Path, "tools with spaces")).FullName;
        await File.WriteAllTextAsync(Path.Combine(tools, "sdk-marker.txt"), "installed", ct);
        var launcher = Path.Combine(tools, OperatingSystem.IsWindows() ? "flutter.bat" : "flutter");
        var script = OperatingSystem.IsWindows()
            ? $"@echo off\r\nif not exist \"%~dp0sdk-marker.txt\" exit /b 42\r\necho SDK found\r\necho %~1\r\necho probe stderr 1>&2\r\nexit /b {exitCode}\r\n"
            : $"#!/bin/sh\ntest -f \"$(dirname \"$0\")/sdk-marker.txt\" || exit 42\nprintf 'SDK found\\n%s\\n' \"$1\"\nprintf 'probe stderr\\n' >&2\nexit {exitCode}\n";
        await File.WriteAllTextAsync(launcher, script, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var entry = OperatingSystem.IsWindows() && quotedPath ? $"\"{tools}\"" : tools;
        try
        {
            Environment.SetEnvironmentVariable("PATH", entry + Path.PathSeparator + originalPath);
            var result = await new SystemProbeEnvironment().RunAsync("flutter", ["argument with spaces"], ct);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal(["SDK found", "argument with spaces"], result.StdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.Equal("probe stderr", result.Stderr.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
