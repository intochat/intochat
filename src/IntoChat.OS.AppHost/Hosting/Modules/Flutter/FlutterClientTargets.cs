using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

/// <summary>
/// Registers explicit-start Flutter client targets (<c>flutter-linux-wsl</c>, <c>flutter-android</c>)
/// for local development so they appear in the Aspire Dashboard with native Start, Stop, and Console Logs.
/// </summary>
internal static class FlutterClientTargets
{
    public const string LinuxWslResourceName = "flutter-linux-wsl";
    public const string AndroidResourceName = "flutter-android";

    internal static void AddExplicitClientTargets(
        IDistributedApplicationBuilder appHost,
        string uiDir,
        IResource parentResource,
        Func<EndpointReference?>? getKernelEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(appHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(uiDir);
        ArgumentNullException.ThrowIfNull(parentResource);

        if (!appHost.ExecutionContext.IsRunMode)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            appHost
                .AddExecutable(
                    LinuxWslResourceName,
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

        var android = appHost
            .AddExecutable(AndroidResourceName, "flutter", uiDir, "run", "-d", "android")
            .WithEnvironment(static context => ChildEnvironmentPolicy.ScrubObjects(context.EnvironmentVariables))
            .WithExplicitStart()
            .ExcludeFromManifest()
            .WithParentRelationship(parentResource)
            .OnBeforeResourceStarted((_, _, ct) => TryConfigureAdbReverseAsync(ct));

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

    private static async Task TryConfigureAdbReverseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var adbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            if (!File.Exists(adbPath))
            {
                return;
            }

            var psi = new ProcessStartInfo(adbPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("reverse");
            psi.ArgumentList.Add("tcp:5080");
            psi.ArgumentList.Add("tcp:5080");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort USB port forwarding; ignore if adb or device is unavailable.
            OsConsoleLog.For(typeof(FlutterClientTargets)).LogDebug(ex, "Skipped adb reverse tcp:5080 setup for Android client.");
        }
    }
}
