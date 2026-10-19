namespace IntoChat.OS.LocalSetup.Checks;

public sealed class DockerEngineCheck : IPreflightCheck
{
    public const string Id = "docker-engine";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var info = await context.DockerInfoAsync(cancellationToken).ConfigureAwait(false);
        return [info is null
            ? CheckResult.Fail(Id, "Docker is not running or not installed.", "Start Docker Desktop (install from https://www.docker.com/products/docker-desktop if missing).")
            : CheckResult.Pass(Id, $"Docker is running ({info.Cpus} CPUs).")];
    }
}

public sealed class LinuxContainersCheck : IPreflightCheck
{
    public const string Id = "linux-containers";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var info = await context.DockerInfoAsync(cancellationToken).ConfigureAwait(false);
        if (info is null) { return []; }
        return [info.OsType == "linux"
            ? CheckResult.Pass(Id, "Docker runs Linux containers.")
            : CheckResult.Fail(Id, $"Docker runs {info.OsType} containers.", "Switch Docker Desktop to Linux containers.")];
    }
}

public sealed class DockerMemoryCheck : IPreflightCheck
{
    public const string Id = "docker-memory";
    public const long WarnBelowBytes = 6L * 1024 * 1024 * 1024;
    public const long FailBelowBytes = 3L * 1024 * 1024 * 1024;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var info = await context.DockerInfoAsync(cancellationToken).ConfigureAwait(false);
        if (info is null) { return []; }
        var text = SizeText.Gibibytes(info.MemTotalBytes);
        const string fix = "Give Docker Desktop at least 6 GiB (Settings > Resources, or memory= in .wslconfig).";
        return [info.MemTotalBytes < FailBelowBytes ? CheckResult.Fail(Id, $"Docker VM has {text} memory; at least 3 GiB is required.", fix)
            : info.MemTotalBytes < WarnBelowBytes ? CheckResult.Warn(Id, $"Docker VM has {text} memory; 6 GiB is recommended.", fix)
            : CheckResult.Pass(Id, $"Docker VM has {text} memory.")];
    }
}
