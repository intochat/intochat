using IntoChat.OS.Aspire.Hosting;
using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

/// <summary>
/// Registers explicit-start Flutter client targets (<c>shell-linux</c>, <c>shell-android</c>)
/// for local development so they appear in the Aspire Dashboard with native Start, Stop, and Console Logs.
/// </summary>
internal static class FlutterClientTargets
{
    public static string LinuxResourceName => ResourceNames.Child(ShellNames.ModuleId, "linux");
    public static string AndroidResourceName => ResourceNames.Child(ShellNames.ModuleId, "android");

    private const int DefaultKernelPort = 5080;

    internal static void AddExplicitClientTargets(
        IDistributedApplicationBuilder appHost,
        string uiDir,
        IResource parentResource,
        FlutterHostOptions? options = null,
        string? presenceToken = null,
        Func<EndpointReference?>? getKernelEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(appHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(uiDir);
        ArgumentNullException.ThrowIfNull(parentResource);

        if (!appHost.ExecutionContext.IsRunMode)
        {
            return;
        }

        var effectiveOptions = options ?? new FlutterHostOptions();
        var flutterCommand = FlutterHostLaunch.ResolveFlutterCommand(effectiveOptions, appHost.Configuration);

        if (OperatingSystem.IsWindows())
        {
            appHost
                .AddExecutable(
                    LinuxResourceName,
                    "wsl",
                    uiDir,
                    "-d",
                    "Ubuntu-24.04",
                    "--",
                    "env",
                    "INTOCHATOS_DESKTOP_PORT=5081",
                    "/home/vlad/.intochat/bin/intochat")
                .WithExplicitStart()
                .ExcludeFromManifest()
                .WithParentRelationship(parentResource);
        }

        string? resolvedDevice = null;

        var android = appHost
            .AddExecutable(AndroidResourceName, flutterCommand, uiDir)
            .WithEnvironment(static context => ChildEnvironmentPolicy.ScrubObjects(context.EnvironmentVariables))
            .WithExplicitStart()
            .ExcludeFromManifest()
            .WithParentRelationship(parentResource)
            .OnBeforeResourceStarted(async (_, _, ct) =>
            {
                var kernelPort = ResolveKernelPort(getKernelEndpoint);
                resolvedDevice = await PrepareAndroidDeviceAsync(
                    effectiveOptions.AndroidTransport,
                    effectiveOptions.AndroidDevice,
                    kernelPort,
                    ct).ConfigureAwait(false);
            })
            .WithArgs(context =>
            {
                context.Args.Add("run");
                context.Args.Add("-d");
                context.Args.Add(string.IsNullOrWhiteSpace(resolvedDevice) ? "android" : resolvedDevice);

                if (effectiveOptions.ReleaseBuild)
                {
                    context.Args.Add("--release");
                }

                var kernelPort = ResolveKernelPort(getKernelEndpoint);
                context.Args.Add($"--dart-define={ShellNames.UIBaseEnvironmentVariable}=http://127.0.0.1:{kernelPort}");

                var shell = string.IsNullOrWhiteSpace(effectiveOptions.ShellName)
                    ? ShellNames.DefaultShellName
                    : effectiveOptions.ShellName;
                var chat = string.IsNullOrWhiteSpace(effectiveOptions.ChatName)
                    ? ShellNames.DefaultChatName
                    : effectiveOptions.ChatName;
                context.Args.Add($"--dart-define={ShellNames.ShellEnvironmentVariable}={shell}");
                context.Args.Add($"--dart-define={ShellNames.ChatEnvironmentVariable}={chat}");

                if (!string.IsNullOrWhiteSpace(presenceToken))
                {
                    context.Args.Add($"{ShellHostingExtensions.PresenceDartDefinePrefix}{presenceToken}");
                }
            })
            .WithCommand(
                "adb-wireless-connect",
                "Connect Wireless ADB",
                async context =>
                {
                    var kernelPort = ResolveKernelPort(getKernelEndpoint);
                    var (connected, message) = await ConnectWirelessAndReverseAsync(
                        effectiveOptions.AndroidDevice,
                        kernelPort,
                        context.CancellationToken).ConfigureAwait(false);
                    if (connected is not null)
                    {
                        resolvedDevice = connected;
                        return CommandResults.Success();
                    }

                    return CommandResults.Failure(message);
                },
                new CommandOptions
                {
                    IconName = "Wifi1",
                    Description = "Discover paired Android devices via ADB mDNS, connect wirelessly, and configure adb reverse.",
                });

        if (getKernelEndpoint is not null)
        {
            android.WithEnvironment(context =>
            {
                if (getKernelEndpoint() is { } kernel)
                {
                    context.EnvironmentVariables[ShellNames.UIBaseEnvironmentVariable] = kernel;
                }
            });
        }
    }

    internal static string? ResolveAdbPath()
    {
        var adbFile = OperatingSystem.IsWindows() ? "adb.exe" : "adb";

        if (FlutterHostLaunch.TryResolveCommandOnPath("adb", out var onPath))
        {
            return onPath;
        }

        string?[] sdkRoots =
        [
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        ];

        foreach (var root in sdkRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = Path.Combine(root.Trim().Trim('"'), "platform-tools", adbFile);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static int ResolveKernelPort(Func<EndpointReference?>? getKernelEndpoint)
    {
        try
        {
            var endpoint = getKernelEndpoint?.Invoke();
            if (endpoint is not null && endpoint.IsAllocated && endpoint.Port > 0)
            {
                return endpoint.Port;
            }
        }
        catch (Exception ex)
        {
            OsConsoleLog.For(typeof(FlutterClientTargets))
                .LogDebug(ex, "Kernel endpoint port is not allocated yet; falling back to default port {Port}.", DefaultKernelPort);
        }

        return DefaultKernelPort;
    }

    private static async Task<string?> PrepareAndroidDeviceAsync(
        AndroidDebugTransport transport,
        string? explicitDevice,
        int kernelPort,
        CancellationToken cancellationToken)
    {
        var logger = OsConsoleLog.For(typeof(FlutterClientTargets));
        var adbPath = ResolveAdbPath();
        if (adbPath is null)
        {
            logger.LogWarning("ADB was not found; skipping wireless connect and adb reverse setup.");
            return explicitDevice;
        }

        if (transport is AndroidDebugTransport.Wireless or AndroidDebugTransport.Auto)
        {
            await TryConnectWirelessDevicesAsync(adbPath, explicitDevice, cancellationToken).ConfigureAwait(false);
        }

        var selected = await ResolveTargetDeviceAsync(adbPath, transport, explicitDevice, cancellationToken)
            .ConfigureAwait(false);

        if (selected is not null)
        {
            logger.LogInformation("Selected Android target '{Device}' (transport: {Transport}).", selected, transport);
            await TryConfigureAdbReverseAsync(adbPath, selected, kernelPort, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            logger.LogWarning(
                "No online Android device matched transport '{Transport}'. Falling back to default Flutter Android target.",
                transport);
            await TryConfigureAdbReverseAsync(adbPath, null, kernelPort, cancellationToken).ConfigureAwait(false);
        }

        return selected;
    }

    private static async Task<(string? Device, string Message)> ConnectWirelessAndReverseAsync(
        string? explicitDevice,
        int kernelPort,
        CancellationToken cancellationToken)
    {
        var adbPath = ResolveAdbPath();
        if (adbPath is null)
        {
            return (null, "ADB executable was not found in PATH or Android SDK platform-tools.");
        }

        var connected = await TryConnectWirelessDevicesAsync(adbPath, explicitDevice, cancellationToken)
            .ConfigureAwait(false);
        var selected = connected.FirstOrDefault()
            ?? await ResolveTargetDeviceAsync(adbPath, AndroidDebugTransport.Wireless, explicitDevice, cancellationToken)
                .ConfigureAwait(false);

        if (selected is null)
        {
            return (null, "No paired wireless Android devices found via ADB mDNS (_adb-tls-connect._tcp).");
        }

        await TryConfigureAdbReverseAsync(adbPath, selected, kernelPort, cancellationToken).ConfigureAwait(false);
        OsConsoleLog.For(typeof(FlutterClientTargets)).LogInformation(
            "Connected wireless Android device '{Device}' and configured adb reverse tcp:{Port}.",
            selected,
            kernelPort);
        return (selected, $"Connected to {selected} (adb reverse tcp:{kernelPort}).");
    }

    private static async Task<IReadOnlyList<string>> TryConnectWirelessDevicesAsync(
        string adbPath,
        string? explicitDevice,
        CancellationToken cancellationToken)
    {
        var connected = new List<string>();
        var logger = OsConsoleLog.For(typeof(FlutterClientTargets));

        var targets = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitDevice) && IsWirelessSerial(explicitDevice))
        {
            targets.Add(explicitDevice.Trim());
        }
        else
        {
            var mdns = await RunAdbAsync(adbPath, ["mdns", "services"], TimeSpan.FromSeconds(4), cancellationToken)
                .ConfigureAwait(false);
            if (mdns.ExitCode == 0)
            {
                targets.AddRange(ParseMdnsConnectEndpoints(mdns.StdOut));
            }
        }

        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = await RunAdbAsync(adbPath, ["connect", target], TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode == 0
                && (result.StdOut.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                    || result.StdOut.Contains("already connected", StringComparison.OrdinalIgnoreCase)))
            {
                connected.Add(target);
                logger.LogInformation("ADB wireless connect succeeded for {Target}.", target);
            }
            else
            {
                logger.LogDebug("ADB wireless connect for {Target} returned: {Output}", target, result.StdOut.Trim());
            }
        }

        return connected;
    }

    private static async Task<string?> ResolveTargetDeviceAsync(
        string adbPath,
        AndroidDebugTransport transport,
        string? explicitDevice,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitDevice))
        {
            return explicitDevice.Trim();
        }

        var devices = await RunAdbAsync(adbPath, ["devices", "-l"], TimeSpan.FromSeconds(4), cancellationToken)
            .ConfigureAwait(false);
        if (devices.ExitCode != 0)
        {
            return null;
        }

        var online = ParseOnlineDeviceSerials(devices.StdOut);
        if (online.Count == 0)
        {
            return null;
        }

        return transport switch
        {
            AndroidDebugTransport.Wireless => online.FirstOrDefault(IsWirelessSerial),
            AndroidDebugTransport.Usb => online.FirstOrDefault(static s => !IsWirelessSerial(s)),
            AndroidDebugTransport.Auto => online.FirstOrDefault(IsWirelessSerial) ?? online[0],
            _ => online[0],
        };
    }

    internal static IReadOnlyList<string> ParseMdnsConnectEndpoints(string output)
    {
        var endpoints = new List<string>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                endpoints.Add(parts[^1]);
            }
        }

        return endpoints;
    }

    internal static IReadOnlyList<string> ParseOnlineDeviceSerials(string output)
    {
        var serials = new List<string>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[1], "device", StringComparison.OrdinalIgnoreCase))
            {
                serials.Add(parts[0]);
            }
        }

        return serials;
    }

    internal static bool IsWirelessSerial(string serial)
        => serial.Contains(':', StringComparison.Ordinal)
            || serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase);

    private static async Task TryConfigureAdbReverseAsync(
        string adbPath,
        string? deviceSerial,
        int kernelPort,
        CancellationToken cancellationToken)
    {
        try
        {
            List<string> args = string.IsNullOrWhiteSpace(deviceSerial)
                ? ["reverse", $"tcp:{kernelPort}", $"tcp:{kernelPort}"]
                : ["-s", deviceSerial, "reverse", $"tcp:{kernelPort}", $"tcp:{kernelPort}"];

            await RunAdbAsync(adbPath, args, TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OsConsoleLog.For(typeof(FlutterClientTargets))
                .LogDebug(ex, "Skipped adb reverse tcp:{Port} setup for Android client.", kernelPort);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAdbAsync(
        string adbPath,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(adbPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "Failed to start adb.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
