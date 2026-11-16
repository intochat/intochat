using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Logging;

namespace IntoChat.OS.LocalSetup;

public sealed class SystemProbeEnvironment : IProbeEnvironment
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    private readonly SocketsHttpHandler _http = new() { ConnectTimeout = TimeSpan.FromSeconds(3) };

    public bool IsWindows => OperatingSystem.IsWindows();

    public HttpMessageHandler Http => _http;

    // Fixed executables, argument list (no shell). Resolve the Windows batch launcher so its %~dp0 points at the SDK.
    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (IsWindows && file == "flutter")
        {
            var launcher = (Env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim('"'), "flutter.bat")).FirstOrDefault(File.Exists);
            file = launcher is null ? "flutter.bat" : Path.GetFullPath(launcher);
        }
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in args) { start.ArgumentList.Add(argument); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessTimeout);
        try
        {
            using var process = Process.Start(start);
            if (process is null) { return new ProcessResult(ProcessResult.NotFound, [], "could not start"); }
            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await copy.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                cancellationToken.ThrowIfCancellationRequested();
                return new ProcessResult(-1, [], "timed out");
            }
            return new ProcessResult(process.ExitCode, buffer.ToArray(), await error.ConfigureAwait(false));
        }
        catch (Win32Exception)
        {
            return new ProcessResult(ProcessResult.NotFound, [], "not found");
        }
    }

    public long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            OsConsoleLog.For<SystemProbeEnvironment>().LogSwallowed(exception, "probing drive free bytes");
            return null;
        }
    }

    public bool CanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public string? Env(string name) => Environment.GetEnvironmentVariable(name);

    public string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            OsConsoleLog.For<SystemProbeEnvironment>().LogSwallowed(exception, "reading optional probe file");
            return null;
        }
    }
}
