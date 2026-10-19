using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>File system operations the reset needs, abstracted so facts never touch the real machine.</summary>
public interface IResetFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    /// <summary>Top-level children with their sizes; no reparse point is followed.</summary>
    IReadOnlyList<(string Name, long Bytes)> Children(string directory);
    bool IsReparsePoint(string path);
    void Delete(string path);
}

/// <summary>Docker operations the reset needs.</summary>
public interface IDockerCli
{
    Task<IReadOnlyList<string>> ContainerNamesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> VolumeNamesAsync(CancellationToken cancellationToken);
    Task RemoveContainerAsync(string name, CancellationToken cancellationToken);
    Task RemoveVolumeAsync(string name, CancellationToken cancellationToken);
}

/// <summary>One thing the reset removes.</summary>
public sealed record ResetTarget(string Kind, string Description, string Reference);

/// <summary>
/// The local-development reset: removes the run state of one checkout (workdir, runtime directory, its Aspire containers and volumes
/// and the shared C# cache volume). Refuses outside Development and run mode, prints everything first, and needs a typed "yes".
/// The master key (user secrets) and the machine id stay unless asked for.
/// </summary>
public sealed class LocalReset(
    IHostEnvironment environment,
    bool isRunMode,
    string repositoryRoot,
    string localApplicationData,
    string appHostPathSha256,
    string? userSecretsDirectory,
    IResetFileSystem fileSystem,
    IDockerCli docker,
    TextReader input,
    TextWriter output,
    bool interactive)
{
    public const string CSharpCacheVolume = "intochat-os-csharp-cache";

    private string WorkDirectory => Path.GetFullPath(Path.Combine(repositoryRoot, ".intochat-os"));
    private string ProductDirectory => Path.GetFullPath(Path.Combine(localApplicationData, "IntoChatOS"));
    private string RuntimeDirectory => Path.Combine(ProductDirectory, "runtime");
    private string MachineIdFile => Path.Combine(ProductDirectory, "machine-id");
    private string Sha10 => appHostPathSha256.Length >= 10 ? appHostPathSha256[..10].ToLowerInvariant() : "";
    private string Sha8 => Sha10.Length >= 8 ? Sha10[..8] : "";

    public async Task<IReadOnlyList<ResetTarget>> BuildPlanAsync(bool includeSecrets, bool includeMachineId, CancellationToken cancellationToken)
    {
        var plan = new List<ResetTarget>();
        if (fileSystem.DirectoryExists(WorkDirectory))
        {
            var children = fileSystem.Children(WorkDirectory);
            plan.Add(new ResetTarget("directory", $"{WorkDirectory} ({children.Count} entries, {children.Sum(c => c.Bytes)} bytes)", WorkDirectory));
        }
        if (fileSystem.DirectoryExists(RuntimeDirectory)) { plan.Add(new ResetTarget("directory", RuntimeDirectory, RuntimeDirectory)); }
        if (includeMachineId && fileSystem.FileExists(MachineIdFile)) { plan.Add(new ResetTarget("file", MachineIdFile + " (machine id)", MachineIdFile)); }
        if (includeSecrets && userSecretsDirectory is { Length: > 0 } && fileSystem.DirectoryExists(userSecretsDirectory))
        { plan.Add(new ResetTarget("secrets", userSecretsDirectory + " (master key and user secrets)", userSecretsDirectory)); }

        // Docker names come only from this checkout's AppHost hash (exact names / hash-prefixed volumes), never from the Aspire label alone.
        var volumes = await docker.VolumeNamesAsync(cancellationToken);
        if (Sha8.Length == 8)
        {
            var containers = await docker.ContainerNamesAsync(cancellationToken);
            var exact = new[] { $"Modules-storage-{Sha8}", $"Modules-ollama-{Sha8}" };
            foreach (var name in containers.Where(c => exact.Contains(c, StringComparer.Ordinal))) { plan.Add(new ResetTarget("container", name, name)); }
            var prefix = $"intochat.os.apphost-{Sha10}-";
            foreach (var name in volumes.Where(v => v.StartsWith(prefix, StringComparison.Ordinal))) { plan.Add(new ResetTarget("volume", name, name)); }
        }
        foreach (var name in volumes.Where(v => v == CSharpCacheVolume))
        { plan.Add(new ResetTarget("volume", name + " (shared by every checkout)", name)); }
        return plan;
    }

    public async Task<int> RunAsync(string[] flags, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || !isRunMode)
        {
            await output.WriteLineAsync("reset refused: it only runs in the Development environment in run mode. Nothing was deleted.");
            return 1;
        }
        var list = flags.Contains("--list");
        var yes = flags.Contains("--yes");
        var plan = await BuildPlanAsync(flags.Contains("--include-secrets"), flags.Contains("--include-machine-id"), cancellationToken);
        await output.WriteLineAsync(plan.Count == 0 ? "Nothing to reset." : "reset would delete:");
        foreach (var target in plan) { await output.WriteLineAsync($"  [{target.Kind}] {target.Description}"); }
        if (list || plan.Count == 0) { return 0; }
        if (!interactive && !yes)
        {
            await output.WriteLineAsync("reset refused: input is not interactive; pass --yes to confirm. Nothing was deleted.");
            return 1;
        }
        if (!yes)
        {
            await output.WriteAsync("Type yes to delete these: ");
            var answer = await input.ReadLineAsync(cancellationToken);
            if (answer != "yes") { await output.WriteLineAsync("aborted"); return 0; }
        }

        foreach (var target in plan.Where(t => t.Kind == "container")) { await docker.RemoveContainerAsync(target.Reference, cancellationToken); }
        foreach (var target in plan.Where(t => t.Kind == "volume")) { await docker.RemoveVolumeAsync(target.Reference, cancellationToken); }
        foreach (var target in plan.Where(t => t.Kind is "directory" or "file" or "secrets"))
        {
            var roots = target.Kind == "secrets" ? new[] { Path.GetFullPath(target.Reference) } : [WorkDirectory, ProductDirectory];
            var full = Path.GetFullPath(target.Reference);
            if (!roots.Any(root => IsRootOrChild(root, full))) { await output.WriteLineAsync($"  skipped (outside the allowed roots): {full}"); continue; }
            if (fileSystem.IsReparsePoint(full)) { await output.WriteLineAsync($"  skipped (reparse point): {full}"); continue; }
            fileSystem.Delete(full);
            await output.WriteLineAsync($"  deleted {full}");
        }
        return 0;
    }

    private static bool IsRootOrChild(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The real file system for <see cref="LocalReset"/>; reparse points are reported, never followed.</summary>
public sealed class RealResetFileSystem : IResetFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public IReadOnlyList<(string Name, long Bytes)> Children(string directory) =>
        [.. new DirectoryInfo(directory).EnumerateFileSystemInfos().Select(info => (info.Name, info is FileInfo file ? file.Length : 0L))];
    public bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    public void Delete(string path)
    {
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } else { File.Delete(path); }
    }
}

/// <summary>The real docker CLI for <see cref="LocalReset"/>, arguments passed as a list.</summary>
public sealed class RealDockerCli : IDockerCli
{
    public Task<IReadOnlyList<string>> ContainerNamesAsync(CancellationToken cancellationToken) => LinesAsync(["ps", "-a", "--format", "{{.Names}}"], cancellationToken);
    public Task<IReadOnlyList<string>> VolumeNamesAsync(CancellationToken cancellationToken) => LinesAsync(["volume", "ls", "--format", "{{.Name}}"], cancellationToken);
    public async Task RemoveContainerAsync(string name, CancellationToken cancellationToken) => await LinesAsync(["rm", "-f", name], cancellationToken);
    public async Task RemoveVolumeAsync(string name, CancellationToken cancellationToken) => await LinesAsync(["volume", "rm", name], cancellationToken);

    private static async Task<IReadOnlyList<string>> LinesAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        try
        {
            using var process = Process.Start(info)!;
            var text = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        }
        catch (System.ComponentModel.Win32Exception) { return []; }
    }
}
