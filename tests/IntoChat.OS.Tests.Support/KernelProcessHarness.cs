using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Support;

public sealed record KernelProcessOptions
{
    public TestHome? Home { get; init; }

    public bool KeepHomeOnDispose { get; init; }

    public Uri? OllamaEndpoint { get; init; }

    public string? DefaultModel { get; init; }

    public bool DisableLocalOllamaProbe { get; init; } = true;

    public bool EnableSandbox { get; init; }

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public IReadOnlyDictionary<string, string?>? ExtraConfiguration { get; init; }
}

/// <summary>
/// Consolidated headless child-process harness for <c>IntoChat.OS.Kernel</c>.
/// Boots the genuine OS product host with <c>SPIKE_WINDOWS=0</c>, an isolated
/// <see cref="TestHome"/>, OS-reserved loopback ports, DPAPI-sealed master key,
/// and scrubbed custody environment.
/// </summary>
public sealed partial class KernelProcessHarness : IAsyncDisposable
{
    private const string ConfigurationFileKey = "IntoChatOS:ConfigurationFile";
    private readonly Process _process;
    private readonly PortLease _ports;
    private readonly bool _keepHomeOnDispose;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly Lock _logLock = new();
    private int _disposed;

    private KernelProcessHarness(
        Process process,
        PortLease ports,
        TestHome home,
        bool keepHomeOnDispose,
        Uri baseAddress,
        string brainId,
        string scope,
        string presenceToken,
        TimeSpan startupDuration)
    {
        _process = process;
        _ports = ports;
        Home = home;
        _keepHomeOnDispose = keepHomeOnDispose;
        BaseAddress = baseAddress;
        BrainId = brainId;
        Scope = scope;
        PresenceToken = presenceToken;
        StartupDuration = startupDuration;
        ProcessId = process.Id;
        Client = CreateClient(withPresence: true);
    }

    public int ProcessId { get; }

    public TestHome Home { get; }

    public Uri BaseAddress { get; }

    public string BrainId { get; }

    public string Scope { get; }

    public string PresenceToken { get; }

    public TimeSpan StartupDuration { get; }

    public HttpClient Client { get; }

    public string ConfigurationFilePath => Home.KernelConfigFilePath;

    public bool HasExited => _process.HasExited;

    public static async Task<KernelProcessHarness> StartAsync(
        KernelProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new KernelProcessOptions();
        var home = options.Home ?? new TestHome();
        var ownsHome = options.Home is null;
        var keepHomeOnDispose = options.KeepHomeOnDispose || !ownsHome;
        PortLease? ports = null;
        Process? process = null;
        KernelProcessHarness? harness = null;

        try
        {
            ports = PortReservation.ReservePair();

            var brainId = AccountSession.DefaultLogin;
            var scope = BrainScope.Create(brainId, brainId).Id;
            var masterKey = home.LoadOrCreateSealedMasterKey();
            var presenceToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PresenceProof.SecretByteCount))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var presenceHash = PresenceProof.ComputeTokenHash(presenceToken);

            WriteRestrictedFile(home.PresenceFilePath, Encoding.UTF8.GetBytes(presenceToken));
            WriteKernelConfigurationFile(home, options, masterKey, presenceHash);

            var (fileName, extraArgs, workingDirectory) = ResolveKernelExecutable();
            var startInfo = BuildStartInfo(
                fileName,
                extraArgs,
                workingDirectory,
                home,
                ports,
                options);

            var stopwatch = Stopwatch.StartNew();
            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var logLock = new Lock();
            var listeningTcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (logLock)
                {
                    stdout.AppendLine(e.Data);
                }

                var match = ListeningLineRegex().Match(e.Data);
                if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
                {
                    listeningTcs.TrySetResult(uri);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (logLock)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            process.Exited += (_, _) =>
            {
                listeningTcs.TrySetException(new InvalidOperationException(
                    $"IntoChat.OS.Kernel exited unexpectedly with code {TryGetExitCode(process)} before binding HTTP.\n"
                    + FormatDiagnostics(stdout, stderr, logLock)));
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start IntoChat.OS.Kernel process '{fileName}'.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(options.ReadyTimeout);

            Uri baseAddress;
            try
            {
                baseAddress = await listeningTcs.Task.WaitAsync(startupCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"IntoChat.OS.Kernel did not report its listening address within {options.ReadyTimeout}.\n"
                    + FormatDiagnostics(stdout, stderr, logLock),
                    ex);
            }

            await WaitForKernelReadyAsync(baseAddress, scope, presenceToken, process, stdout, stderr, logLock, options.ReadyTimeout, startupCts.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();

            harness = new KernelProcessHarness(
                process,
                ports,
                home,
                keepHomeOnDispose,
                baseAddress,
                brainId,
                scope,
                presenceToken,
                stopwatch.Elapsed);

            lock (logLock)
            {
                lock (harness._logLock)
                {
                    harness._stdout.Append(stdout);
                    harness._stderr.Append(stderr);
                }
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (harness._logLock)
                {
                    harness._stdout.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (harness._logLock)
                {
                    harness._stderr.AppendLine(e.Data);
                }
            };

            return harness;
        }
        catch
        {
            if (harness is not null)
            {
                await harness.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                if (process is not null)
                {
                    TryKillProcess(process);
                    process.Dispose();
                }

                ports?.Dispose();
                if (!keepHomeOnDispose)
                {
                    home.Dispose();
                }
            }

            throw;
        }
    }

    public HttpClient CreateClient(bool withPresence = true)
    {
        var client = new HttpClient
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (withPresence)
        {
            client.DefaultRequestHeaders.Add(PresenceProof.HeaderName, PresenceToken);
        }

        return client;
    }

    public async Task KillAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _ports.Dispose();
        }
    }

    public string ReadDiagnostics()
        => FormatDiagnostics(_stdout, _stderr, _logLock);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!TryIsExited(_process))
            {
                try
                {
                    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await Client.PostAsync("/runtime/shutdown", content: null, shutdownCts.Token)
                        .ConfigureAwait(false);
                    await _process.WaitForExitAsync(shutdownCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    TryKillProcess(_process);
                }
            }
        }
        finally
        {
            Client.Dispose();
            _process.Dispose();
            _ports.Dispose();
            if (!_keepHomeOnDispose)
            {
                Home.Dispose();
            }
        }
    }

    private static async Task WaitForKernelReadyAsync(
        Uri baseAddress,
        string scope,
        string presenceToken,
        Process process,
        StringBuilder stdout,
        StringBuilder stderr,
        Lock logLock,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var probeClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        probeClient.DefaultRequestHeaders.Add(PresenceProof.HeaderName, presenceToken);

        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastFailure = null;
        string[] readinessPaths =
        [
            "/ready",
            "/health",
            $"/brains/{Uri.EscapeDataString(scope)}/apps",
        ];

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"IntoChat.OS.Kernel exited with code {TryGetExitCode(process)} while waiting for readiness.\n"
                    + FormatDiagnostics(stdout, stderr, logLock));
            }

            try
            {
                var allReady = true;
                foreach (var path in readinessPaths)
                {
                    using var response = await probeClient.GetAsync(path, ct).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        allReady = false;
                        lastFailure = new HttpRequestException($"GET {path} returned {(int)response.StatusCode} ({response.StatusCode}).");
                        break;
                    }
                }

                if (allReady)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastFailure = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"IntoChat.OS.Kernel did not become ready within {timeout}. Last probe error: {lastFailure?.Message}\n"
            + FormatDiagnostics(stdout, stderr, logLock),
            lastFailure);
    }

    private static ProcessStartInfo BuildStartInfo(
        string fileName,
        IReadOnlyList<string> extraArgs,
        string workingDirectory,
        TestHome home,
        PortLease ports,
        KernelProcessOptions options)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add($"--{ConfigurationFileKey}={home.KernelConfigFilePath}");

        ChildEnvironmentPolicy.Scrub(psi);

        psi.Environment["SPIKE_WINDOWS"] = "0";
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Production";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["OTEL_SDK_DISABLED"] = "true";
        psi.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        psi.Environment["Logging__Console__FormatterName"] = "simple";

        psi.Environment[ToEnvKey(IntoChatOSNames.HomeKey)] = home.Path;
        psi.Environment[ToEnvKey(IntoChatOSNames.TopologyKey)] = IntoChatOSNames.TopologyLocal;
        psi.Environment[ToEnvKey(IntoChatOSNames.ShellEnabledKey)] = "false";
        psi.Environment[ToEnvKey(IntoChatOSNames.FrontEnabledKey)] = "false";
        psi.Environment[ToEnvKey(IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey)] = "true";
        psi.Environment["IntoChatOS__Auth__Posture"] = "Open";
        psi.Environment["IntoChatOS__Authority__Identity__Posture"] = "Open";
        psi.Environment["IntoChatOS__OrleansDashboard__Enabled"] = "false";
        psi.Environment["OrleansDashboard__Enabled"] = "false";
        psi.Environment["IntoChatOS__Modules__sandbox__Enabled"] = options.EnableSandbox ? "true" : "false";
        psi.Environment[ToEnvKey(TopologyContext.SiloPortKey)] = ports.SiloPort.ToString(CultureInfo.InvariantCulture);
        psi.Environment[ToEnvKey(TopologyContext.GatewayPortKey)] = ports.GatewayPort.ToString(CultureInfo.InvariantCulture);

        if (options.DisableLocalOllamaProbe)
        {
            psi.Environment["IntoChatOS__Modules__devices__Options__FoundryLocal__Enabled"] = "false";
            if (options.OllamaEndpoint is null)
            {
                psi.Environment["IntoChatOS__Modules__devices__Options__Ollama__Enabled"] = "false";
            }
        }

        return psi;
    }

    private static string ToEnvKey(string configurationKey)
        => configurationKey.Replace(":", "__", StringComparison.Ordinal);

    private static void WriteKernelConfigurationFile(
        TestHome home,
        KernelProcessOptions options,
        string masterKey,
        string presenceHash)
    {
        var root = new JsonObject();
        SetNestedValue(root, IntoChatOSNames.MasterKeyConfigurationKey, masterKey);
        SetNestedValue(root, PresenceProof.TokenFileConfigurationKey, home.PresenceFilePath);
        SetNestedValue(root, PresenceProof.TokenHashConfigurationKey, presenceHash);

        if (options.OllamaEndpoint is not null)
        {
            SetNestedValue(root, AIKeys.OllamaEndpoint, options.OllamaEndpoint.GetLeftPart(UriPartial.Authority));
            SetNestedValue(root, "IntoChatOS:Modules:devices:Options:Ollama:ProbeTimeoutMs", "10000");
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultModel))
        {
            SetNestedValue(root, AIKeys.Role(IntoChat.OS.Modules.AI.Models.Where.Device, IntoChat.OS.Modules.AI.Models.ModelRole.Chat), options.DefaultModel);
            SetNestedValue(root, AIKeys.Role(IntoChat.OS.Modules.AI.Models.Where.Cloud, IntoChat.OS.Modules.AI.Models.ModelRole.Chat), options.DefaultModel);
        }

        if (options.ExtraConfiguration is not null)
        {
            foreach (var (key, value) in options.ExtraConfiguration)
            {
                SetNestedValue(root, key, value);
            }
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteRestrictedFile(home.KernelConfigFilePath, Encoding.UTF8.GetBytes(json));
    }

    private static void SetNestedValue(JsonObject root, string colonKey, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(colonKey);
        var parts = colonKey.Split(':');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject child)
            {
                child = new JsonObject();
                current[parts[i]] = child;
            }

            current = child;
        }

        current[parts[^1]] = value is null ? null : JsonValue.Create(value);
    }

    private static void WriteRestrictedFile(string targetPath, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);
        TestHome.RestrictDirectoryToCurrentUser(dir);

        var tempPath = $"{targetPath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        File.WriteAllBytes(tempPath, bytes);
        TestHome.RestrictFileToCurrentUser(tempPath);
        File.Move(tempPath, targetPath, overwrite: true);
        TestHome.RestrictFileToCurrentUser(targetPath);
    }

    private static (string FileName, string[] ExtraArgs, string WorkingDirectory) ResolveKernelExecutable()
    {
        var repoRoot = TestHome.RepoRoot();
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var candidateDirs = new List<string> { AppContext.BaseDirectory };
        if (!string.IsNullOrWhiteSpace(baseDir.Name))
        {
            candidateDirs.Add(Path.Combine(repoRoot, "artifacts", "bin", "IntoChat.OS.Kernel", baseDir.Name));
        }

        var exeName = OperatingSystem.IsWindows() ? "IntoChat.OS.Kernel.exe" : "IntoChat.OS.Kernel";
        foreach (var dir in candidateDirs)
        {
            var exePath = Path.Combine(dir, exeName);
            if (File.Exists(exePath))
            {
                return (exePath, [], dir);
            }

            var dllPath = Path.Combine(dir, "IntoChat.OS.Kernel.dll");
            if (File.Exists(dllPath))
            {
                return ("dotnet", [dllPath], dir);
            }
        }

        throw new FileNotFoundException(
            $"Unable to locate built IntoChat.OS.Kernel binary in any of: {string.Join(", ", candidateDirs)}.");
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryIsExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
        }
    }

    private static string FormatDiagnostics(StringBuilder stdout, StringBuilder stderr, Lock logLock)
    {
        lock (logLock)
        {
            return $"--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}";
        }
    }

    [GeneratedRegex(@"Now listening on:\s*(https?://\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListeningLineRegex();
}
