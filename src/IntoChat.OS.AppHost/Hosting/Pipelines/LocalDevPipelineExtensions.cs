#pragma warning disable ASPIREPIPELINES001
using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Registers Aspire pipeline steps for local development maintenance (<c>reset</c>, <c>build-android</c>).
/// </summary>
public static class LocalDevPipelineExtensions
{
    public const string ResetStepName = "reset";
    public const string BuildAndroidStepName = "build-android";

    public static IDistributedApplicationBuilder AddLocalDevPipeline(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Pipeline.AddStep(ResetStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Resetting local IntoChat OS checkout state", context.CancellationToken);
            var exitCode = await LocalReset.RunFromAppHostAsync(builder, ["--yes"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Local reset failed with exit code {exitCode}.");
            }
            await task.SucceedAsync("Local checkout state reset.", context.CancellationToken);
        });

        builder.Pipeline.AddStep(BuildAndroidStepName, async context =>
        {
            var flutterOptions = FlutterHostOptions.Overlay(new FlutterHostOptions(), builder.Configuration);
            var isRelease = builder.Configuration.GetValue<bool>("Android:Release") || flutterOptions.ReleaseBuild;
            var mode = isRelease ? "release" : "debug";
            var uiDir = ShellHostingExtensions.ResolveFlutterWorkingDirectory(builder.AppHostDirectory, flutterOptions.WorkingDirectory);
            var flutterCommand = FlutterHostLaunch.ResolveFlutterCommand(flutterOptions, builder.Configuration);

            await using var task = await context.ReportingStep.CreateTaskAsync(
                $"Building IntoChat Flutter Android APK ({mode})",
                context.CancellationToken);

            var psi = new ProcessStartInfo(flutterCommand)
            {
                WorkingDirectory = uiDir,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("apk");
            psi.ArgumentList.Add(isRelease ? "--release" : "--debug");

            var uiBase = builder.Configuration["Android:UiBase"];
            if (!string.IsNullOrWhiteSpace(uiBase))
            {
                psi.ArgumentList.Add($"--dart-define={ShellNames.UIBaseEnvironmentVariable}={uiBase}");
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start '{flutterCommand}'.");
            await process.WaitForExitAsync(context.CancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Flutter Android APK build failed with exit code {process.ExitCode}.");
            }

            var apkPath = Path.Combine(uiDir, "build", "app", "outputs", "flutter-apk", $"app-{mode}.apk");
            await task.SucceedAsync($"Built Android APK: {apkPath}", context.CancellationToken);
        });

        return builder;
    }
}
