using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Logging;
using IntoChat.Sdk;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Helpers converting between portable <see cref="AppPackage"/> definitions, <see cref="AppBundle"/> files,
/// and <see cref="MarketplaceListing"/> metadata, plus an in-process <see cref="ISandboxRuntimeBridge"/> fallback.
/// </summary>
internal static class AppPackageExtensions
{
    public const string PackageManifestFile = "package.json";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static AppBundle ToAppBundle(this AppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PackageManifestFile] = System.Text.Encoding.UTF8.GetString(DigitalBrain.Invocation.RuntimeProtocol.SerializeToUtf8Bytes(package, typeof(AppPackage))),
        };

        foreach (var file in package.SourceFiles)
        {
            files[file.Path] = file.Code;
        }

        var operations = package.Needs.Commands
            .Select(cmd => new AppOperation(cmd.Name, string.IsNullOrWhiteSpace(cmd.Description) ? cmd.Name : cmd.Description))
            .ToArray();

        var manifest = new AppManifest(
            package.Title,
            package.Summary,
            operations,
            [],
            [],
            Icon: package.Icon);
        return new AppBundle(package.Key, package.Version, manifest, package.Summary, files);
    }

    public static MarketplaceListing ToMarketplaceListing(this AppPackage package, AppSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var publisher = string.IsNullOrWhiteSpace(package.Author) ? ShippedPackages.Publisher : package.Author;
        return new MarketplaceListing(
            publisher,
            source ?? new AppSource.Shipped(),
            package.Key,
            package.Title,
            package.Summary,
            package.Version,
            package.Icon,
            Package: package);
    }

    public static AppPackage? TryFromFiles(string appKey, string version, IReadOnlyDictionary<string, string>? files, string? fallbackTitle = null, string? fallbackSummary = null, string? fallbackAuthor = null)
    {
        if (files is null || files.Count == 0)
        {
            return null;
        }

        if (files.TryGetValue(PackageManifestFile, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (DigitalBrain.Invocation.RuntimeProtocol.Deserialize(System.Text.Encoding.UTF8.GetBytes(json), typeof(AppPackage)) is AppPackage parsed)
                {
                    return parsed;
                }
            }
            catch (JsonException error)
            {
                OsConsoleLog.For(typeof(AppPackageExtensions)).LogSwallowed(error, "Malformed package manifest JSON; synthesizing AppPackage from source files.");
            }
        }

        var csFiles = files
            .Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && kv.Value.Contains("BrainApp<", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AppSourceFile(kv.Key, kv.Value))
            .ToArray();

        if (csFiles.Length == 0)
        {
            return null;
        }

        return new AppPackage(
            appKey,
            string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
            string.IsNullOrWhiteSpace(fallbackTitle) ? appKey : fallbackTitle,
            fallbackSummary ?? "",
            "apps",
            "Automation",
            fallbackAuthor ?? "",
            csFiles,
            AppPackageNeeds.Empty,
            "{}",
            AppPackage.ComputeDigest(appKey, string.IsNullOrWhiteSpace(version) ? "1.0.0" : version, csFiles));
    }
}

/// <summary>
/// Default fallback <see cref="ISandboxRuntimeBridge"/> used in unit tests and headless silos when no external
/// container URL is configured. Tracks deployments cleanly without executing untrusted dynamic assemblies on host.
/// </summary>
internal sealed class NullSandboxRuntimeBridge : ISandboxRuntimeBridge
{
    private readonly ConcurrentDictionary<string, (AppPackage Package, string? StateJson, string? SettingsJson)> _deployments = new(StringComparer.Ordinal);

    public Task<SandboxDeploymentResult> DeployPackageAsync(
        string brainId,
        AppPackage package,
        string? settingsJson,
        string? stateJson,
        CancellationToken cancellationToken = default)
        => DeployPackageAsync(brainId, package, [], stateJson, settingsJson, cancellationToken);

    public Task<SandboxDeploymentResult> DeployPackageAsync(
        string brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = assemblyBytes;
        _deployments[DeploymentKey(brainId, package.Key)] = (package, stateJson, settingsJson);
        return Task.FromResult(new SandboxDeploymentResult(
            Succeeded: true,
            Error: null,
            package.Needs.Signals,
            package.Needs.Schedules,
            [.. package.Needs.Commands.Select(c => c.Name)],
            new DigitalBrain.UI.View(package.Title, new DigitalBrain.UI.Column([new DigitalBrain.UI.Text(package.Summary)]))));
    }

    public Task<SandboxDispatchResult> DispatchSignalAsync(string brainId, string appKey, string signalName, string payloadJson, CancellationToken cancellationToken = default)
        => Task.FromResult(new SandboxDispatchResult(Handled: false, null, null, null, "External sandbox URL is not configured."));

    public Task<SandboxDispatchResult> DispatchScheduleAsync(string brainId, string appKey, string cron, CancellationToken cancellationToken = default)
        => Task.FromResult(new SandboxDispatchResult(Handled: false, null, null, null, "External sandbox URL is not configured."));

    public Task<SandboxDispatchResult> DispatchCommandAsync(string brainId, string appKey, string command, string? payloadJson = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new SandboxDispatchResult(Handled: false, null, null, null, "External sandbox URL is not configured."));

    public Task<DigitalBrain.UI.View?> RenderAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
    {
        if (_deployments.TryGetValue(DeploymentKey(brainId, appKey), out var entry))
        {
            return Task.FromResult<DigitalBrain.UI.View?>(
                new DigitalBrain.UI.View(entry.Package.Title, new DigitalBrain.UI.Column([new DigitalBrain.UI.Text(entry.Package.Summary)])));
        }

        return Task.FromResult<DigitalBrain.UI.View?>(null);
    }

    public Task ForwardSignalAsync(string brainId, string signalName, string payloadJson, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
    {
        _deployments.TryRemove(DeploymentKey(brainId, appKey), out _);
        return Task.CompletedTask;
    }

    private static string DeploymentKey(string brainId, string appKey)
        => new DigitalBrain.NeuronId(brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId), "app", appKey).FullKey();
}
