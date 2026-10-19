namespace IntoChat.OS.LocalSetup.Checks;

public sealed class DiskCheck : IPreflightCheck
{
    public const long WarnBelowBytes = 20L * 1024 * 1024 * 1024;
    public const long FailBelowBytes = 10L * 1024 * 1024 * 1024;

    public Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var environment = context.Environment;
        var models = environment.Env("OLLAMA_MODELS");
        if (string.IsNullOrWhiteSpace(models))
        {
            var home = environment.Env(environment.IsWindows ? "USERPROFILE" : "HOME");
            models = string.IsNullOrWhiteSpace(home) ? context.Options.RepositoryRoot : Path.Combine(home, ".ollama", "models");
        }
        IReadOnlyList<CheckResult> results =
        [
            Evaluate("disk-repo", "the repository drive", environment.FreeBytes(context.Options.RepositoryRoot)),
            Evaluate("disk-models", "the Ollama models drive", environment.FreeBytes(models)),
        ];
        return Task.FromResult(results);
    }

    private static CheckResult Evaluate(string id, string where, long? free)
    {
        const string fix = "Free disk space; local setup needs about 20 GiB for container images and the model.";
        return free switch
        {
            null => CheckResult.Warn(id, $"Could not read free space on {where}.", fix),
            < FailBelowBytes => CheckResult.Fail(id, $"Only {SizeText.Gibibytes(free.Value)} free on {where}; at least 10 GiB is required.", fix),
            < WarnBelowBytes => CheckResult.Warn(id, $"{SizeText.Gibibytes(free.Value)} free on {where}; 20 GiB is recommended.", fix),
            _ => CheckResult.Pass(id, $"{SizeText.Gibibytes(free.Value)} free on {where}."),
        };
    }
}
