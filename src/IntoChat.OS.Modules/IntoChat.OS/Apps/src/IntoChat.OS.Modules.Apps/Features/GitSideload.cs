using System.Diagnostics;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Storage.Confinement;

namespace IntoChat.OS.Modules.Apps;

public delegate Task<int> GitRunner(GitCloneRequest request, CancellationToken cancellationToken);

public sealed record GitCloneRequest(IReadOnlyList<string> Arguments, string AllowProtocol, TimeSpan Timeout);

// Clones one https or ssh URL into the apps directory. The argument list is built before the process
// starts, so a URL is never a git option. Tests pass a runner; the host uses the git on PATH.
public sealed class GitSideload
{
    public const long MaxBytes = 50L * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public const string AllowProtocol = "https:ssh";

    private static readonly Regex Scp = new("^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^-]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly GitRunner _runner;
    private readonly long _maxBytes;

    public GitSideload()
        : this(Run, MaxBytes) { }

    public GitSideload(GitRunner runner, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _maxBytes = maxBytes;
    }

    public static IReadOnlyList<string> Arguments(string url, string target) =>
    [
        "-c", "core.hooksPath=/dev/null",
        "-c", "protocol.file.allow=never",
        "-c", "protocol.ext.allow=never",
        "-c", "protocol.fd.allow=never",
        "-c", "protocol.allow=user",
        "clone", "--depth", "1", "--no-tags", "--", url, target,
    ];

    public static void RequireUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        url = url.Trim();
        if (url.StartsWith('-')) { throw new ArgumentException("A git URL cannot start with '-'."); }
        if (url.Contains("::", StringComparison.Ordinal)) { throw new ArgumentException("A git URL cannot name a transport helper."); }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh") { return; }
        if (Scp.IsMatch(url)) { return; }
        throw new ArgumentException("A git URL must be https or ssh.");
    }

    public async Task<string> CloneInto(string appsDirectory, string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appsDirectory);
        var trimmed = url.Trim();
        RequireUrl(trimmed);
        var root = PathConfinement.Normalize(appsDirectory);
        Directory.CreateDirectory(root);
        PathConfinement.RequireNoLinks(root);
        var staging = Path.Combine(root, "_clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var code = await _runner(new GitCloneRequest(Arguments(trimmed, staging), AllowProtocol, Timeout), cancellationToken).ConfigureAwait(false);
            if (code != 0) { throw new InvalidOperationException("git clone failed."); }
            Confine(staging);
            if (Size(staging) > _maxBytes) { throw new InvalidOperationException($"The clone is larger than {_maxBytes} bytes."); }
            var git = Path.Combine(staging, ".git");
            if (Directory.Exists(git)) { Directory.Delete(git, recursive: true); }
            return staging;
        }
        catch
        {
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
            throw;
        }
    }

    public static async Task<int> Run(GitCloneRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Arguments) { start.ArgumentList.Add(argument); }
        start.Environment["GIT_ALLOW_PROTOCOL"] = request.AllowProtocol;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) { throw new InvalidOperationException("git did not start. Install git and leave it on PATH."); }
        }
        catch (System.ComponentModel.Win32Exception error)
        { throw new InvalidOperationException("git did not start. Install git and leave it on PATH.", error); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            { _ = error.Message; }
            throw new TimeoutException("The git clone timed out.");
        }
        return process.ExitCode;
    }

    private static void Confine(string staging)
    {
        var root = PathConfinement.Normalize(staging);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            PathConfinement.RequireNoLinks(entry);
            _ = PathConfinement.Resolve([root], PathConfinement.Normalize(entry));
        }
    }

    private static long Size(string staging)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        { total += new FileInfo(file).Length; }
        return total;
    }
}
