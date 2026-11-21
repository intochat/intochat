using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;

internal sealed class SherpaOnnxRuntime : ILocalRuntime, IVoiceActivityDetector, IDisposable
{
    public const string HttpClientName = "devices.sherpa-onnx";

    private readonly Func<DevicesOptions> _optionsAccessor;
    private readonly ISherpaApi _api;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<SherpaOnnxRuntime> _logger;
    private readonly SherpaModelAssets _assets;
    private readonly Lock _gate = new();
    private KokoroSpeechClient? _speakClient;
    private bool _disposed;

    public SherpaOnnxRuntime(
        IOptionsMonitor<DevicesOptions> options,
        ISherpaApi api,
        IHttpClientFactory http,
        TimeProvider clock,
        ILogger<SherpaOnnxRuntime> logger)
        : this(() => options.CurrentValue, api, http, clock, logger, SherpaModelAssets.Default)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    internal SherpaOnnxRuntime(
        DevicesOptions options,
        ISherpaApi api,
        IHttpClientFactory http,
        TimeProvider clock,
        ILogger<SherpaOnnxRuntime> logger,
        SherpaModelAssets? assets = null)
        : this(() => options, api, http, clock, logger, assets ?? SherpaModelAssets.Default)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    private SherpaOnnxRuntime(
        Func<DevicesOptions> optionsAccessor,
        ISherpaApi api,
        IHttpClientFactory http,
        TimeProvider clock,
        ILogger<SherpaOnnxRuntime> logger,
        SherpaModelAssets assets)
    {
        _optionsAccessor = optionsAccessor;
        _api = api;
        _http = http;
        _clock = clock;
        _logger = logger;
        _assets = assets;
    }

    public RuntimeId Id => RuntimeId.SherpaOnnx;

    public bool Ready => IsInstalled();

    private string ModelDirectory
    {
        get
        {
            var configured = _optionsAccessor().SherpaOnnx?.ModelDirectory;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".intochat",
                "models",
                "sherpa-onnx");
        }
    }

    private string KokoroDirectory => Path.Combine(ModelDirectory, SherpaModels.KokoroDirectoryName);

    private string KokoroModelFile => Path.Combine(KokoroDirectory, SherpaModels.KokoroModelFileName);

    private string VadModelFile => Path.Combine(ModelDirectory, SherpaModels.VadFileName);

    public Task<RuntimeStatus> Probe(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeStatus(Id, Reachable: true, Version: SherpaModels.SdkVersion, Endpoint: null));
    }

    public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield return KnownModels.Kokoro82M;
    }

    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        if (IsInstalled())
        {
            yield return KnownModels.Kokoro82M;
        }
    }

    public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
    {
        if (id != KnownModels.Kokoro82M.Id)
        {
            yield return new InstallProgress(id, InstallState.Failed, 0, 0, 0, $"sherpa-onnx does not serve model '{id.Value}'.");
            yield break;
        }

        var rootDir = ModelDirectory;
        Directory.CreateDirectory(rootDir);
        var totalBytes = _assets.TotalBytes;
        var stagingDir = Path.Combine(rootDir, ".staging-" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(stagingDir, "extract");
        var archiveFile = Path.Combine(stagingDir, SherpaModels.KokoroArchiveName);
        var vadTempFile = Path.Combine(stagingDir, SherpaModels.VadFileName);
        Directory.CreateDirectory(extractDir);

        string? failure = null;
        long downloadedTotal = 0;

        try
        {
            yield return new InstallProgress(id, InstallState.Downloading, 0, 0, totalBytes);

            using var client = _http.CreateClient(HttpClientName);

            // Download Kokoro archive
            var kokoroChannel = new List<InstallProgress>();
            try
            {
                downloadedTotal = await DownloadFileAsync(
                    client,
                    _assets.KokoroArchiveUrl,
                    archiveFile,
                    baseBytes: 0,
                    totalBytes,
                    bytes => kokoroChannel.Add(ProgressOf(id, InstallState.Downloading, bytes, totalBytes)),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Failed downloading Kokoro model archive from {Url}.", _assets.KokoroArchiveUrl);
                failure = $"Download failed: {error.Message}";
            }

            foreach (var p in kokoroChannel)
            {
                yield return p;
            }

            if (failure is not null)
            {
                yield return new InstallProgress(id, InstallState.Failed, 0, downloadedTotal, totalBytes, failure);
                yield break;
            }

            // Download Silero VAD file
            var vadChannel = new List<InstallProgress>();
            try
            {
                downloadedTotal = await DownloadFileAsync(
                    client,
                    _assets.VadUrl,
                    vadTempFile,
                    baseBytes: downloadedTotal,
                    totalBytes,
                    bytes => vadChannel.Add(ProgressOf(id, InstallState.Downloading, bytes, totalBytes)),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Failed downloading Silero VAD model from {Url}.", _assets.VadUrl);
                failure = $"Download failed: {error.Message}";
            }

            foreach (var p in vadChannel)
            {
                yield return p;
            }

            if (failure is not null)
            {
                yield return new InstallProgress(id, InstallState.Failed, 0, downloadedTotal, totalBytes, failure);
                yield break;
            }

            // Verify SHA-256 checksums before extracting or touching target paths
            yield return new InstallProgress(id, InstallState.Verifying, 99, downloadedTotal, totalBytes);

            var actualArchiveSha = await ComputeSha256Async(archiveFile, ct).ConfigureAwait(false);
            if (!string.Equals(actualArchiveSha, _assets.KokoroArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                yield return new InstallProgress(
                    id,
                    InstallState.Failed,
                    0,
                    downloadedTotal,
                    totalBytes,
                    $"Kokoro archive checksum mismatch (expected {_assets.KokoroArchiveSha256}, got {actualArchiveSha}).");
                yield break;
            }

            var actualVadSha = await ComputeSha256Async(vadTempFile, ct).ConfigureAwait(false);
            if (!string.Equals(actualVadSha, _assets.VadSha256, StringComparison.OrdinalIgnoreCase))
            {
                yield return new InstallProgress(
                    id,
                    InstallState.Failed,
                    0,
                    downloadedTotal,
                    totalBytes,
                    $"Silero VAD checksum mismatch (expected {_assets.VadSha256}, got {actualVadSha}).");
                yield break;
            }

            // Extract Kokoro archive into temp folder and move into place atomically
            try
            {
                await ExtractTarBz2Async(archiveFile, extractDir, ct).ConfigureAwait(false);
                var extractedKokoro = Path.Combine(extractDir, SherpaModels.KokoroDirectoryName);
                var extractedModel = Path.Combine(extractedKokoro, SherpaModels.KokoroModelFileName);
                if (!File.Exists(extractedModel))
                {
                    failure = $"Extracted Kokoro archive did not contain {SherpaModels.KokoroDirectoryName}/{SherpaModels.KokoroModelFileName}.";
                }
                else
                {
                    ResetSpeakClient();
                    var targetKokoro = KokoroDirectory;
                    var backupKokoro = targetKokoro + ".old-" + _clock.GetUtcNow().ToUnixTimeMilliseconds();
                    if (Directory.Exists(targetKokoro))
                    {
                        Directory.Move(targetKokoro, backupKokoro);
                    }

                    try
                    {
                        Directory.Move(extractedKokoro, targetKokoro);
                        File.Move(vadTempFile, VadModelFile, overwrite: true);
                    }
                    catch
                    {
                        if (!Directory.Exists(targetKokoro) && Directory.Exists(backupKokoro))
                        {
                            Directory.Move(backupKokoro, targetKokoro);
                        }

                        throw;
                    }

                    TryDeleteDirectory(backupKokoro);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Failed extracting Kokoro model archive.");
                failure = $"Extract failed: {error.Message}";
            }

            if (failure is not null)
            {
                yield return new InstallProgress(id, InstallState.Failed, 0, downloadedTotal, totalBytes, failure);
                yield break;
            }

            yield return new InstallProgress(id, InstallState.Ready, 100, totalBytes, totalBytes);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    public Task Remove(ModelId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id != KnownModels.Kokoro82M.Id)
        {
            return Task.CompletedTask;
        }

        ResetSpeakClient();
        TryDeleteDirectory(KokoroDirectory);
        TryDeleteFile(VadModelFile);
        return Task.CompletedTask;
    }

    public IChatClient Chat(ModelId id)
        => throw new NotSupportedException("sherpa-onnx serves speech only.");

    public ITextToSpeechClient? Speak(ModelId id)
    {
        if (id != KnownModels.Kokoro82M.Id || !IsInstalled())
        {
            return null;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _speakClient ??= new KokoroSpeechClient(() => _api.CreateTts(KokoroDirectory));
        }
    }

    public IVoiceActivity Open(int sampleRate = 16_000)
    {
        if (!IsInstalled())
        {
            throw new InvalidOperationException("Silero VAD is not installed. Install Kokoro 82M first.");
        }

        return new SileroVoiceActivity(_api.CreateVad(VadModelFile, sampleRate));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        ResetSpeakClient();
    }

    private bool IsInstalled()
        => File.Exists(KokoroModelFile) && File.Exists(VadModelFile);

    private void ResetSpeakClient()
    {
        KokoroSpeechClient? existing;
        lock (_gate)
        {
            existing = _speakClient;
            _speakClient = null;
        }

        existing?.Dispose();
    }

    private static InstallProgress ProgressOf(ModelId id, InstallState state, long bytes, long totalBytes)
    {
        var pct = totalBytes > 0 ? Math.Clamp(bytes * 100.0 / totalBytes, 0.0, 99.0) : 0.0;
        return new InstallProgress(id, state, pct, bytes, totalBytes);
    }

    private static async Task<long> DownloadFileAsync(
        HttpClient client,
        Uri url,
        string destination,
        long baseBytes,
        long totalBytes,
        Action<long> report,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true);

        var buffer = new byte[81_920];
        long written = 0;
        long lastReported = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (written - lastReported >= 1_048_576 || written == totalBytes)
            {
                lastReported = written;
                report(baseBytes + written);
            }
        }

        var finalTotal = baseBytes + written;
        report(finalTotal);
        return finalTotal;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task ExtractTarBz2Async(string archivePath, string destinationDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tar")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-xjf");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(destinationDir);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start OS tar executable.");
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"tar -xjf exited with code {proc.ExitCode}: {stderr.Trim()}");
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(error, "Could not delete sherpa-onnx directory {Path}.", path);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(error, "Could not delete sherpa-onnx file {Path}.", path);
        }
    }
}
