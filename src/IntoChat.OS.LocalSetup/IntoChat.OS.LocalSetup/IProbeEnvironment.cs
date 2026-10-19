namespace IntoChat.OS.LocalSetup;

public sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public const int NotFound = 127;
    public string StdoutText => System.Text.Encoding.UTF8.GetString(Stdout);
}

// Everything preflight observes about the machine goes through here, so tests and --fake never touch the real one.
public interface IProbeEnvironment
{
    bool IsWindows { get; }
    HttpMessageHandler Http { get; }
    Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken);
    long? FreeBytes(string path);
    bool CanBind(int port);
    string? Env(string name);
    string? ReadFile(string path);
}
