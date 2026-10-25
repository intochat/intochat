using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Integrations;

namespace IntoChat.OS.Testing;

// A built server host (for example IntoChat.OS.Kernel.dll) run as a real child process on Topology=local over a
// TestHome, on fixed loopback ports reserved for the host's lifetime, so it can be killed and launched again over
// the same Home, ports and keys. Output goes to a log file; the master key travels only in the child's environment.
public sealed class ChildProcessHost : IAsyncDisposable
{
    private readonly string _assembly;
    private readonly PortReservation _ports;
    private readonly string _masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string _runTokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly IReadOnlyDictionary<string, string> _settings;
    private Process? _process;
    private StreamWriter? _log;

    public ChildProcessHost(string assembly, IReadOnlyDictionary<string, string>? settings = null)
    {
        if (!File.Exists(assembly)) { throw new FileNotFoundException("Build the host before launching it as a child process.", assembly); }
        _assembly = assembly;
        _settings = settings ?? new Dictionary<string, string>();
        _ports = PortReservation.Reserve(3);
        LogPath = System.IO.Path.Combine(Home.Path, "host.log");
    }

    public TestHome Home { get; } = TestHome.Create();
    public int SiloPort => _ports.Ports[0];
    public int GatewayPort => _ports.Ports[1];
    public Uri BaseAddress => new($"http://127.0.0.1:{_ports.Ports[2].ToString(CultureInfo.InvariantCulture)}/");
    public string LogPath { get; }
    public int? ProcessId => _process?.Id;

    public async Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) { throw new InvalidOperationException("The child host is already running."); }
        _ports.ReleaseSockets();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(_assembly)!,
        };
        start.ArgumentList.Add(_assembly);
        foreach (var (key, value) in Environment())
        { start.Environment[ConfigurationKeyGrammar.EnvironmentName(key)] = value; }
        var log = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _log = log;
        await log.WriteLineAsync($"--- launch {DateTimeOffset.UtcNow:O}").ConfigureAwait(false);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, line) => { if (line.Data is { } data) { Append(log, data); } };
        process.ErrorDataReceived += (_, line) => { if (line.Data is { } data) { Append(log, data); } };
        process.Exited += (_, _) => Append(log, $"--- exited {DateTimeOffset.UtcNow:O}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) { throw new InvalidOperationException($"The child host exited with {process.ExitCode} before it was healthy; see {LogPath}."); }
            try
            {
                using var alive = await http.GetAsync("alive", cancellationToken).ConfigureAwait(false);
                if (alive.IsSuccessStatusCode) { return; }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            if (DateTimeOffset.UtcNow > deadline) { throw new TimeoutException($"The child host was not healthy within {timeout}; see {LogPath}."); }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    // A hard kill of the whole process tree: no graceful shutdown, no deactivation, no flush.
    public async Task KillAsync()
    {
        if (_process is not { } process) { return; }
        _process = null;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { }
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.Dispose();
        if (Interlocked.Exchange(ref _log, null) is { } log) { lock (log) { log.Dispose(); } }
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync().ConfigureAwait(false);
        _ports.Dispose();
        Home.Dispose();
    }

    // Output can still arrive while a kill closes the log; that tail is dropped.
    private static void Append(StreamWriter log, string line)
    {
        lock (log)
        {
            try { log.WriteLine(line); }
            catch (ObjectDisposedException) { }
        }
    }

    private IEnumerable<KeyValuePair<string, string>> Environment()
    {
        yield return new("ASPNETCORE_ENVIRONMENT", "Testing");
        yield return new("ASPNETCORE_URLS", BaseAddress.GetLeftPart(UriPartial.Authority));
        yield return new(IntoChatOSNames.TopologyKey, IntoChatOSNames.TopologyLocal);
        yield return new(IntoChatOSNames.HomeKey, Home.Path);
        yield return new(IntoChatOSNames.MasterKeyConfigurationKey, _masterKey);
        yield return new(IntegrationSchema.Key("csharp", "RunTokenKey"), _runTokenKey);
        yield return new(AuthOptions.PostureKey, "Open");
        yield return new(global::DigitalBrain.Runtime.Orleans.TopologyContext.SiloPortKey, SiloPort.ToString(CultureInfo.InvariantCulture));
        yield return new(global::DigitalBrain.Runtime.Orleans.TopologyContext.GatewayPortKey, GatewayPort.ToString(CultureInfo.InvariantCulture));
        foreach (var setting in _settings) { yield return setting; }
    }
}
