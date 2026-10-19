using System.Globalization;

namespace IntoChat.OS.LocalSetup;

public sealed record PreflightOptions(LocalModelOptions Model, bool ShellEnabled, string RepositoryRoot);

public sealed record DockerInfo(string OsType, long MemTotalBytes, int Cpus);

public interface IPreflightCheck
{
    Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken);
}

// What one preflight run shares between checks: the probe environment, the options, and the single `docker info` answer.
public sealed class PreflightContext(IProbeEnvironment environment, PreflightOptions options)
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);
    private Task<DockerInfo?>? _docker;

    public IProbeEnvironment Environment { get; } = environment;

    public PreflightOptions Options { get; } = options;

    public Task<DockerInfo?> DockerInfoAsync(CancellationToken cancellationToken) => _docker ??= LoadDockerInfoAsync(cancellationToken);

    // Null on any transport failure or non-success status; bodies are only returned for success.
    public async Task<string?> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(Environment.Http, disposeHandler: false) { Timeout = HttpTimeout };
        try
        {
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<DockerInfo?> LoadDockerInfoAsync(CancellationToken cancellationToken)
    {
        var result = await Environment.RunAsync("docker", ["info", "--format", "{{.OSType}} {{.MemTotal}} {{.NCPU}}"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) { return null; }
        var parts = result.StdoutText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var memory)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var cpus))
        {
            return null;
        }
        return new DockerInfo(parts[0].ToLowerInvariant(), memory, cpus);
    }
}
