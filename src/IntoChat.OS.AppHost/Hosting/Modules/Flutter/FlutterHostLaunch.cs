using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

internal static class FlutterHostLaunch
{
    private const string ShellPackageDirectoryName = "shell";
    private const string DefaultFlutterCommand = "flutter";

    internal sealed record Result(
        string Command,
        string WorkingDirectory,
        string[] Args,
        string? DeviceTarget = null);

    internal static Result Resolve(
        FlutterHostKind kind,
        string packageRoot,
        FlutterHostOptions options,
        IConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(options);

        return kind switch
        {
            FlutterHostKind.Window => ResolveWindow(packageRoot, options, configuration),
            FlutterHostKind.Web => ResolveWeb(packageRoot, options, configuration),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static string? ResolveWindowPackageDirectory(string packageRoot, string deviceTarget)
    {
        if (HasWindowMarkers(packageRoot, deviceTarget))
        {
            return NormalizeDriveLetter(Path.GetFullPath(packageRoot));
        }

        var nestedShell = Path.Combine(packageRoot, ShellPackageDirectoryName);
        if (HasWindowMarkers(nestedShell, deviceTarget))
        {
            return NormalizeDriveLetter(Path.GetFullPath(nestedShell));
        }

        var siblingShell = Path.GetFullPath(Path.Combine(packageRoot, "..", ShellPackageDirectoryName));
        if (HasWindowMarkers(siblingShell, deviceTarget))
        {
            return NormalizeDriveLetter(siblingShell);
        }

        return null;
    }

    private static Result ResolveWindow(
        string packageRoot,
        FlutterHostOptions options,
        IConfiguration? configuration)
    {
        var deviceTarget = string.IsNullOrWhiteSpace(options.DeviceTarget)
            || string.Equals(options.DeviceTarget, ShellNames.DefaultDeviceTarget, StringComparison.OrdinalIgnoreCase)
            ? ShellNames.CurrentHostDeviceTarget
            : options.DeviceTarget;
        var workDir = ResolveWindowPackageDirectory(packageRoot, deviceTarget)
            ?? throw new InvalidOperationException(
                $"Window Flutter host needs lib/main.dart and a '{deviceTarget}/' folder " +
                $"under '{packageRoot}'.");

        if (string.Equals(deviceTarget, "windows", StringComparison.OrdinalIgnoreCase))
        {
            HealWindowsEphemeral(workDir, deviceTarget);
        }

        var args = new List<string> { "run", "-d", deviceTarget };
        if (options.ReleaseBuild)
        {
            args.Add("--release");
        }

        return new Result(
            ResolveFlutterCommand(options, configuration),
            workDir,
            [.. args],
            deviceTarget);
    }

    internal static string? ResolveWebPackageDirectory(string packageRoot)
    {
        if (HasWebMarkers(packageRoot))
        {
            return NormalizeDriveLetter(Path.GetFullPath(packageRoot));
        }

        var nestedShell = Path.Combine(packageRoot, ShellPackageDirectoryName);
        if (HasWebMarkers(nestedShell))
        {
            return NormalizeDriveLetter(Path.GetFullPath(nestedShell));
        }

        var siblingShell = Path.GetFullPath(Path.Combine(packageRoot, "..", ShellPackageDirectoryName));
        if (HasWebMarkers(siblingShell))
        {
            return NormalizeDriveLetter(siblingShell);
        }

        return null;
    }

    private static Result ResolveWeb(
        string packageRoot,
        FlutterHostOptions options,
        IConfiguration? configuration)
    {
        var deviceTarget = string.IsNullOrWhiteSpace(options.DeviceTarget)
            || string.Equals(options.DeviceTarget, ShellNames.DefaultDeviceTarget, StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.DeviceTarget, "windows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.DeviceTarget, "macos", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.DeviceTarget, "linux", StringComparison.OrdinalIgnoreCase)
            ? ShellNames.DefaultWebDeviceTarget
            : options.DeviceTarget;
        var workDir = ResolveWebPackageDirectory(packageRoot)
            ?? throw new InvalidOperationException(
                $"Web Flutter host needs lib/main.dart and a 'web/' folder " +
                $"under '{packageRoot}'. " +
                $"Use {nameof(ShellHostingExtensions.RunDesktopApp)}() for Windows chrome.");

        // Both web devices (web-server and chrome) honor the port/hostname flags, so the served
        // address always matches the fixed Aspire endpoint ShellHostingExtensions registers.
        //
        // The headless web-server device must run --release: its debug build only executes
        // main() through a one-shot DWDS handshake granted to the first browser instance that
        // connects after the compile finishes -- a connection arriving mid-compile (the dev
        // server answers "/" with 200 within seconds of launch, long before the build lands) or
        // any second instance (a plain browser refresh) loads all scripts but never starts the
        // app. Proven live against Flutter 3.44.8; a release build serves plain static script
        // bootstrapping with none of that fragility. Debug + hot reload stays available through
        // a browser-driving device target (e.g. "chrome" via the configure hook).
        var args = new List<string> { "run", "-d", deviceTarget };
        if (string.Equals(deviceTarget, ShellNames.DefaultWebDeviceTarget, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--release");
        }

        args.Add($"--web-hostname={ShellNames.FlutterWebHostname}");
        return new Result(ResolveFlutterCommand(options, configuration), workDir, [.. args], deviceTarget);
    }

    private static bool HasWindowMarkers(string workingDirectory, string deviceTarget)
    {
        var mainDart = Path.Combine(workingDirectory, "lib", "main.dart");
        if (!File.Exists(mainDart))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(workingDirectory, deviceTarget));
    }

    internal static string NormalizeDriveLetter(string path)
    {
        if (OperatingSystem.IsWindows()
            && path.Length >= 2
            && path[1] == ':'
            && char.IsAsciiLetterLower(path[0]))
        {
            return string.Concat(char.ToUpperInvariant(path[0]).ToString(), path.AsSpan(1));
        }

        return path;
    }

    private static void HealWindowsEphemeral(string workingDirectory, string deviceTarget)
    {
        var ephemeralDir = Path.Combine(workingDirectory, deviceTarget, "flutter", "ephemeral");
        var generatedConfig = Path.Combine(ephemeralDir, "generated_config.cmake");
        if (File.Exists(generatedConfig))
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var expectedBackslash = $"\"PROJECT_DIR={workingDirectory.Replace("\\", "\\\\", StringComparison.Ordinal)}\"";
            var expectedSlash = $"\"PROJECT_DIR={workingDirectory.Replace('\\', '/')}\"";
            var config = File.ReadAllText(generatedConfig);
            if (!config.Contains(expectedBackslash, comparison) && !config.Contains(expectedSlash, comparison))
            {
                if (Directory.Exists(ephemeralDir))
                {
                    Directory.Delete(ephemeralDir, recursive: true);
                }
                var buildDir = Path.Combine(workingDirectory, "build", deviceTarget);
                if (Directory.Exists(buildDir))
                {
                    Directory.Delete(buildDir, recursive: true);
                }
            }
        }

        var symlinksDir = Path.Combine(ephemeralDir, ".plugin_symlinks");
        if (Directory.Exists(symlinksDir))
        {
            foreach (var entry in Directory.EnumerateDirectories(symlinksDir))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                {
                    Directory.Delete(symlinksDir, recursive: true);
                    break;
                }
            }
        }

        var engineDll = Path.Combine(ephemeralDir, "flutter_windows.dll");
        var wrapperSource = Path.Combine(ephemeralDir, "cpp_client_wrapper", "core_implementations.cc");
        if (!File.Exists(engineDll) || !File.Exists(wrapperSource))
        {
            var lastBuildId = Path.Combine(workingDirectory, "build", ".last_build_id");
            if (File.Exists(lastBuildId))
            {
                File.Delete(lastBuildId);
            }

            var flutterBuildCache = Path.Combine(workingDirectory, ".dart_tool", "flutter_build");
            if (Directory.Exists(flutterBuildCache))
            {
                Directory.Delete(flutterBuildCache, recursive: true);
            }
        }
    }

    private static bool HasWebMarkers(string workingDirectory)
    {
        var mainDart = Path.Combine(workingDirectory, "lib", "main.dart");
        if (!File.Exists(mainDart))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(workingDirectory, ShellNames.WebPlatformDirectoryName))
            && File.Exists(Path.Combine(
                workingDirectory,
                ShellNames.WebPlatformDirectoryName,
                "index.html"));
    }

    internal static string ResolveFlutterCommand(
        FlutterHostOptions options,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (TryConfiguredCommand(options.FlutterCommand, out var fromOptions))
        {
            return fromOptions;
        }

        if (TryConfiguredCommand(configuration is null ? null : FlutterHostOptions.Overlay(new FlutterHostOptions(), configuration).FlutterCommand, out var fromConfig))
        {
            return fromConfig;
        }

        if (TryConfiguredCommand(
                Environment.GetEnvironmentVariable(
                    ShellNames.FlutterCommandEnvironmentVariable),
                out var fromEnv))
        {
            return fromEnv;
        }

        if (TryResolveCommandOnPath(DefaultFlutterCommand, out var fromPath))
        {
            return fromPath;
        }

        return DefaultFlutterCommand;
    }

    private static bool TryConfiguredCommand(string? value, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        command = value.Trim().Trim('"');
        return true;
    }

    internal static bool TryResolveCommandOnPath(string commandName, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(commandName) || Path.IsPathRooted(commandName))
        {
            if (!string.IsNullOrWhiteSpace(commandName)
                && Path.IsPathRooted(commandName)
                && File.Exists(commandName))
            {
                absolutePath = Path.GetFullPath(commandName);
                return true;
            }

            return false;
        }

        var names = CommandFileNames(commandName);
        foreach (var directory in PathSearchDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    absolutePath = Path.GetFullPath(candidate);
                    return true;
                }
            }
        }

        return false;
    }

    private static string[] CommandFileNames(string commandName)
    {
        if (!OperatingSystem.IsWindows()
            || commandName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || commandName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || commandName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || commandName.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
        {
            return [commandName];
        }

        return
        [
            commandName + ".exe",
            commandName + ".cmd",
            commandName + ".bat",
            commandName + ".com",
            commandName,
        ];
    }

    private static IEnumerable<string> PathSearchDirectories()
    {
        foreach (var directory in SplitPath(Environment.GetEnvironmentVariable("PATH")))
        {
            yield return directory;
        }

        foreach (var directory in SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
        {
            yield return directory;
        }

        foreach (var directory in SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)))
        {
            yield return directory;
        }

        var flutterRoot = Environment.GetEnvironmentVariable("FLUTTER_ROOT");
        if (!string.IsNullOrWhiteSpace(flutterRoot))
        {
            yield return Path.Combine(flutterRoot.Trim().Trim('"'), "bin");
        }
    }

    private static IEnumerable<string> SplitPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = segment.Trim().Trim('"');
            if (directory.Length > 0)
            {
                yield return directory;
            }
        }
    }
}
