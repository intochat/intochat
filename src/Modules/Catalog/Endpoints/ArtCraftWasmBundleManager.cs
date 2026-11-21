using System.IO.Compression;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.ArtCraft;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// On-demand downloader & cache manager for ArtCraft Rust WASM release bundles
/// (<see cref="ArtCraftAppDescriptor.WebReleaseZipUrl"/>).
/// </summary>
internal static class ArtCraftWasmBundleManager
{
    private static readonly ILogger Log = OsConsoleLog.For(typeof(ArtCraftWasmBundleManager));
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    public sealed record BundleInstallResult(bool Installed, string? DirectoryPath, string? Error = null);

    public static string GetBundleDirectory(ArtCraftAppDescriptor descriptor, string? cacheRoot = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? ArtCraftWebEndpoints.GetCacheRootDirectory() : cacheRoot;
        var version = string.IsNullOrWhiteSpace(descriptor.WebReleaseVersion) ? "latest" : descriptor.WebReleaseVersion;
        return Path.Combine(root, $"{descriptor.Id}-{version}");
    }

    public static async Task<BundleInstallResult> EnsureInstalledAsync(
        ArtCraftAppDescriptor descriptor,
        string? cacheRoot = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var targetDir = GetBundleDirectory(descriptor, cacheRoot);
        if (File.Exists(Path.Combine(targetDir, "index.html")))
        {
            return new BundleInstallResult(true, targetDir);
        }

        if (string.IsNullOrWhiteSpace(descriptor.WebReleaseZipUrl))
        {
            return new BundleInstallResult(false, null, $"No WASM release URL configured for '{descriptor.Title}'.");
        }

        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(Path.Combine(targetDir, "index.html")))
            {
                return new BundleInstallResult(true, targetDir);
            }

            var parentDir = Path.GetDirectoryName(targetDir)!;
            Directory.CreateDirectory(parentDir);

            var stagingDir = Path.Combine(parentDir, $".staging-{descriptor.Id}-{Guid.NewGuid():N}");
            var zipPath = Path.Combine(parentDir, $".download-{descriptor.Id}-{Guid.NewGuid():N}.zip");
            try
            {
                var client = httpClient ?? SharedHttp;
                using var response = await client.GetAsync(descriptor.WebReleaseZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new BundleInstallResult(
                        false,
                        null,
                        $"Failed to download {descriptor.Title} WASM bundle (HTTP {(int)response.StatusCode}).");
                }

                await using (var fs = File.Create(zipPath))
                {
                    await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                Directory.CreateDirectory(stagingDir);
                ExtractZipSafely(zipPath, stagingDir);

                var resolvedRoot = ResolveExtractedIndexRoot(stagingDir);
                if (resolvedRoot is null)
                {
                    return new BundleInstallResult(false, null, $"Downloaded archive for '{descriptor.Title}' did not contain an index.html.");
                }

                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, recursive: true);
                }

                Directory.Move(resolvedRoot, targetDir);
                return new BundleInstallResult(true, targetDir);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogWarning(ex, "Failed to install {Title} WASM bundle from {Url}.", descriptor.Title, descriptor.WebReleaseZipUrl);
                return new BundleInstallResult(false, null, $"Failed to install {descriptor.Title} WASM bundle: {ex.Message}");
            }
            finally
            {
                TryDeleteFile(zipPath);
                TryDeleteDirectory(stagingDir);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static void ExtractZipSafely(string zipPath, string destinationDir)
    {
        var fullDest = Path.GetFullPath(destinationDir);
        if (!fullDest.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDest += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) { continue; }
            var candidate = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
            if (!candidate.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Refusing path-traversal zip entry '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
            entry.ExtractToFile(candidate, overwrite: true);
        }
    }

    private static string? ResolveExtractedIndexRoot(string stagingDir)
    {
        if (File.Exists(Path.Combine(stagingDir, "index.html")))
        {
            return stagingDir;
        }

        var subDirs = Directory.GetDirectories(stagingDir);
        if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "index.html")))
        {
            return subDirs[0];
        }

        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch (IOException ex)
        {
            Log.LogDebug(ex, "Could not clean up temporary ArtCraft zip file {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogDebug(ex, "Could not clean up temporary ArtCraft zip file {Path}.", path);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
        catch (IOException ex)
        {
            Log.LogDebug(ex, "Could not clean up temporary ArtCraft staging directory {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogDebug(ex, "Could not clean up temporary ArtCraft staging directory {Path}.", path);
        }
    }
}
