using IntoChat.OS.Aspire.Hosting;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

/// <summary>
/// Registers explicit-start Flutter client targets under <c>flutter</c>
/// (<c>flutter-windows</c>, <c>flutter-linux</c>, <c>flutter-macos</c>, <c>flutter-android</c>, <c>flutter-ios</c>, <c>flutter-web</c>)
/// for local development so every platform appears in the Aspire Dashboard with native Start, Stop, and Console Logs.
/// </summary>
internal static class FlutterClientTargets
{
    public static string WindowsResourceName => ShellNames.WindowsResourceName;
    public static string LinuxResourceName => ShellNames.LinuxResourceName;
    public static string MacOsResourceName => ShellNames.MacOsResourceName;
    public static string AndroidResourceName => ShellNames.AndroidResourceName;
    public static string IosResourceName => ShellNames.IosResourceName;
    public static string WebResourceName => ShellNames.WebResourceName;

    internal sealed record IosDeviceCandidate(
        string Id,
        string Name,
        IosDebugTransport Transport);

    private const int DefaultKernelPort = 5080;

    internal static void AddExplicitClientTargets(
        IDistributedApplicationBuilder appHost,
        string uiDir,
        IResource parentResource,
        string? primaryResourceName = null,
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

        bool HasTarget(string name)
            => string.Equals(primaryResourceName, name, StringComparison.OrdinalIgnoreCase)
                || appHost.Resources.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        if (!HasTarget(WindowsResourceName))
        {
            AddSimpleFlutterTarget(appHost, WindowsResourceName, "windows", uiDir, parentResource, flutterCommand, effectiveOptions, presenceToken, getKernelEndpoint);
        }

        if (!HasTarget(LinuxResourceName))
        {
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
            else
            {
                AddSimpleFlutterTarget(appHost, LinuxResourceName, "linux", uiDir, parentResource, flutterCommand, effectiveOptions, presenceToken, getKernelEndpoint);
            }
        }

        if (!HasTarget(MacOsResourceName))
        {
            AddSimpleFlutterTarget(appHost, MacOsResourceName, "macos", uiDir, parentResource, flutterCommand, effectiveOptions, presenceToken, getKernelEndpoint);
        }

        if (!HasTarget(WebResourceName))
        {
            AddSimpleFlutterTarget(appHost, WebResourceName, ShellNames.DefaultWebDeviceTarget, uiDir, parentResource, flutterCommand, effectiveOptions, presenceToken, getKernelEndpoint);
        }

        string? resolvedDevice = null;

        if (!HasTarget(AndroidResourceName))
        {
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

        if (!HasTarget(IosResourceName))
        {
            AddIosClientTarget(appHost, uiDir, parentResource, flutterCommand, effectiveOptions, presenceToken, getKernelEndpoint);
        }
    }

    private static void AddSimpleFlutterTarget(
        IDistributedApplicationBuilder appHost,
        string resourceName,
        string deviceTarget,
        string uiDir,
        IResource parentResource,
        string flutterCommand,
        FlutterHostOptions options,
        string? presenceToken,
        Func<EndpointReference?>? getKernelEndpoint)
    {
        var target = appHost
            .AddExecutable(resourceName, flutterCommand, uiDir)
            .WithEnvironment(static context => ChildEnvironmentPolicy.ScrubObjects(context.EnvironmentVariables))
            .WithExplicitStart()
            .ExcludeFromManifest()
            .WithParentRelationship(parentResource)
            .WithArgs(context =>
            {
                context.Args.Add("run");
                context.Args.Add("-d");
                context.Args.Add(deviceTarget);
                if (options.ReleaseBuild || string.Equals(deviceTarget, ShellNames.DefaultWebDeviceTarget, StringComparison.OrdinalIgnoreCase))
                {
                    context.Args.Add("--release");
                }

                if (!string.IsNullOrWhiteSpace(presenceToken))
                {
                    context.Args.Add($"{ShellHostingExtensions.PresenceDartDefinePrefix}{presenceToken}");
                }
            });

        if (getKernelEndpoint is not null)
        {
            target.WithEnvironment(context =>
            {
                if (getKernelEndpoint() is { } kernel)
                {
                    context.EnvironmentVariables[ShellNames.UIBaseEnvironmentVariable] = kernel;
                }
            });
        }
    }

    private static void AddIosClientTarget(
        IDistributedApplicationBuilder appHost,
        string uiDir,
        IResource parentResource,
        string flutterCommand,
        FlutterHostOptions options,
        string? presenceToken,
        Func<EndpointReference?>? getKernelEndpoint)
    {
        IosDeviceCandidate? resolvedIosDevice = null;

        var ios = appHost
            .AddExecutable(IosResourceName, flutterCommand, uiDir)
            .WithEnvironment(static context => ChildEnvironmentPolicy.ScrubObjects(context.EnvironmentVariables))
            .WithExplicitStart()
            .ExcludeFromManifest()
            .WithParentRelationship(parentResource)
            .OnBeforeResourceStarted(async (_, _, ct) =>
            {
                resolvedIosDevice = await ResolveIosTargetDeviceAsync(
                    options.IosTransport,
                    options.IosDevice,
                    ct).ConfigureAwait(false);
            })
            .WithArgs(context =>
            {
                context.Args.Add("run");
                context.Args.Add("-d");
                var deviceId = resolvedIosDevice?.Id
                    ?? (!string.IsNullOrWhiteSpace(options.IosDevice) ? options.IosDevice.Trim() : "ios");
                context.Args.Add(deviceId);

                if (options.ReleaseBuild)
                {
                    context.Args.Add("--release");
                }

                var kernelPort = ResolveKernelPort(getKernelEndpoint);
                var isSimulator = resolvedIosDevice?.Transport == IosDebugTransport.Simulator;
                var uiBase = ResolveIosUiBase(options.IosUiBase, kernelPort, isSimulator);
                context.Args.Add($"--dart-define={ShellNames.UIBaseEnvironmentVariable}={uiBase}");

                var shell = string.IsNullOrWhiteSpace(options.ShellName)
                    ? ShellNames.DefaultShellName
                    : options.ShellName;
                var chat = string.IsNullOrWhiteSpace(options.ChatName)
                    ? ShellNames.DefaultChatName
                    : options.ChatName;
                context.Args.Add($"--dart-define={ShellNames.ShellEnvironmentVariable}={shell}");
                context.Args.Add($"--dart-define={ShellNames.ChatEnvironmentVariable}={chat}");

                if (!string.IsNullOrWhiteSpace(presenceToken))
                {
                    context.Args.Add($"{ShellHostingExtensions.PresenceDartDefinePrefix}{presenceToken}");
                }
            })
            .WithCommand(
                "ios-wireless-discover",
                "Discover Wireless iOS",
                async context =>
                {
                    var selected = await ResolveIosTargetDeviceAsync(
                        IosDebugTransport.Wireless,
                        options.IosDevice,
                        context.CancellationToken).ConfigureAwait(false);
                    if (selected is not null)
                    {
                        resolvedIosDevice = selected;
                        OsConsoleLog.For(typeof(FlutterClientTargets)).LogInformation(
                            "Discovered wireless iOS device '{Name}' ({Id}).",
                            selected.Name,
                            selected.Id);
                        return CommandResults.Success();
                    }

                    return CommandResults.Failure(
                        "No paired wireless iOS devices found via xcrun devicectl. Pair once over USB in Xcode (Devices and Simulators -> Connect via network).");
                },
                new CommandOptions
                {
                    IconName = "Wifi1",
                    Description = "Discover paired iOS devices available over Wi-Fi via Xcode CoreDevice (xcrun devicectl).",
                });

        if (getKernelEndpoint is not null)
        {
            ios.WithEnvironment(context =>
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
            DigitalBrain.Log.Swallow.LogSwallowed(IntoChat.OS.Host.Logging.OsConsoleLog.For(typeof(FlutterClientTargets)), ex, $"running adb '{adbPath}'");
            return (-1, string.Empty, ex.Message);
        }
    }

    private static async Task<IosDeviceCandidate?> ResolveIosTargetDeviceAsync(
        IosDebugTransport transport,
        string? explicitDevice,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitDevice))
        {
            return new IosDeviceCandidate(explicitDevice.Trim(), explicitDevice.Trim(), transport);
        }

        var candidates = new List<IosDeviceCandidate>();

        if (transport is IosDebugTransport.Auto or IosDebugTransport.Wireless or IosDebugTransport.Usb)
        {
            var devicectlJson = await TryRunDevicectlListAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(devicectlJson))
            {
                candidates.AddRange(ParseDevicectlDevices(devicectlJson));
            }
        }

        if (transport is IosDebugTransport.Auto or IosDebugTransport.Simulator)
        {
            var simctl = await RunProcessAsync(
                "xcrun",
                ["simctl", "list", "devices", "booted", "--json"],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            if (simctl.ExitCode == 0 && !string.IsNullOrWhiteSpace(simctl.StdOut))
            {
                candidates.AddRange(ParseSimctlBootedDevices(simctl.StdOut));
            }
        }

        var selected = SelectIosDevice(candidates, transport);
        if (selected is not null)
        {
            OsConsoleLog.For(typeof(FlutterClientTargets)).LogInformation(
                "Selected iOS target '{Name}' ({Id}, transport: {Transport}).",
                selected.Name,
                selected.Id,
                selected.Transport);
        }

        return selected;
    }

    private static async Task<string?> TryRunDevicectlListAsync(CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"intochat-devicectl-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunProcessAsync(
                "xcrun",
                ["devicectl", "list", "devices", "--json-output", tempFile],
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 && File.Exists(tempFile))
            {
                return await File.ReadAllTextAsync(tempFile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            OsConsoleLog.For(typeof(FlutterClientTargets))
                .LogDebug(ex, "Skipped xcrun devicectl iOS device discovery.");
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception ex)
            {
                OsConsoleLog.For(typeof(FlutterClientTargets))
                    .LogDebug(ex, "Failed to delete temporary devicectl output '{Path}'.", tempFile);
            }
        }

        return null;
    }

    internal static IReadOnlyList<IosDeviceCandidate> ParseDevicectlDevices(string json)
    {
        var devices = new List<IosDeviceCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("devices", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return devices;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("hardwareProperties", out var hw)
                    || !hw.TryGetProperty("udid", out var udidEl))
                {
                    continue;
                }

                var udid = udidEl.GetString();
                if (string.IsNullOrWhiteSpace(udid))
                {
                    continue;
                }

                var platform = hw.TryGetProperty("platform", out var platEl) ? platEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(platform)
                    && !platform.Contains("iOS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = item.TryGetProperty("deviceProperties", out var dp)
                    && dp.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString() ?? udid
                    : udid;

                var transportType = item.TryGetProperty("connectionProperties", out var cp)
                    && cp.TryGetProperty("transportType", out var ttEl)
                    ? ttEl.GetString()
                    : null;

                var pairingState = item.TryGetProperty("connectionProperties", out var cp2)
                    && cp2.TryGetProperty("pairingState", out var psEl)
                    ? psEl.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(pairingState)
                    && !string.Equals(pairingState, "paired", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var transport = string.Equals(transportType, "localNetwork", StringComparison.OrdinalIgnoreCase)
                    ? IosDebugTransport.Wireless
                    : IosDebugTransport.Usb;

                devices.Add(new IosDeviceCandidate(udid, name, transport));
            }
        }
        catch (JsonException ex)
        {
            OsConsoleLog.For(typeof(FlutterClientTargets))
                .LogDebug(ex, "Failed to parse xcrun devicectl JSON output.");
        }

        return devices;
    }

    internal static IReadOnlyList<IosDeviceCandidate> ParseSimctlBootedDevices(string json)
    {
        var devices = new List<IosDeviceCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("devices", out var runtimeMap)
                || runtimeMap.ValueKind != JsonValueKind.Object)
            {
                return devices;
            }

            foreach (var runtime in runtimeMap.EnumerateObject())
            {
                if (!runtime.Name.Contains("iOS", StringComparison.OrdinalIgnoreCase)
                    || runtime.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var sim in runtime.Value.EnumerateArray())
                {
                    var state = sim.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : null;
                    if (!string.Equals(state, "Booted", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var udid = sim.TryGetProperty("udid", out var udidEl) ? udidEl.GetString() : null;
                    var name = sim.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : udid;
                    if (!string.IsNullOrWhiteSpace(udid))
                    {
                        devices.Add(new IosDeviceCandidate(udid, name ?? udid, IosDebugTransport.Simulator));
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            OsConsoleLog.For(typeof(FlutterClientTargets))
                .LogDebug(ex, "Failed to parse xcrun simctl JSON output.");
        }

        return devices;
    }

    internal static IosDeviceCandidate? SelectIosDevice(
        IReadOnlyList<IosDeviceCandidate> candidates,
        IosDebugTransport transport)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        return transport switch
        {
            IosDebugTransport.Wireless => candidates.FirstOrDefault(static d => d.Transport == IosDebugTransport.Wireless),
            IosDebugTransport.Usb => candidates.FirstOrDefault(static d => d.Transport == IosDebugTransport.Usb),
            IosDebugTransport.Simulator => candidates.FirstOrDefault(static d => d.Transport == IosDebugTransport.Simulator),
            IosDebugTransport.Auto =>
                candidates.FirstOrDefault(static d => d.Transport == IosDebugTransport.Wireless)
                ?? candidates.FirstOrDefault(static d => d.Transport == IosDebugTransport.Usb)
                ?? candidates.FirstOrDefault(static d => d.Transport == IosDebugTransport.Simulator),
            _ => candidates[0],
        };
    }

    internal static string ResolveIosUiBase(string? configuredUiBase, int kernelPort, bool isSimulator)
    {
        if (!string.IsNullOrWhiteSpace(configuredUiBase))
        {
            return configuredUiBase.Trim();
        }

        if (isSimulator)
        {
            return $"http://127.0.0.1:{kernelPort}";
        }

        var lanIp = TryGetHostLanIpv4();
        return lanIp is not null
            ? $"http://{lanIp}:{kernelPort}"
            : $"http://127.0.0.1:{kernelPort}";
    }

    private static string? TryGetHostLanIpv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OsConsoleLog.For(typeof(FlutterClientTargets))
                .LogDebug(ex, "Could not determine host LAN IPv4 address for iOS client.");
        }

        return null;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string command,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => RunAdbAsync(command, args, timeout, cancellationToken);
}
