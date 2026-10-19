using System.Text.Json;

namespace IntoChat.OS.LocalSetup.Checks;

public sealed class OllamaCheck : IPreflightCheck
{
    public const string Id = "ollama";
    public const string InstallUrl = "https://ollama.com/download";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var body = await context.GetAsync(new Uri(context.Options.Model.EndpointUri, "/api/version"), cancellationToken).ConfigureAwait(false);
        return [body is null
            ? CheckResult.Fail(Id, "Ollama is not reachable.", $"Install Ollama from {InstallUrl} and start it.")
            : CheckResult.Pass(Id, "Ollama is running.")];
    }
}

public sealed class ModelPresentCheck : IPreflightCheck
{
    public const string Id = "model";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var model = context.Options.Model.Model;
        var body = await context.GetAsync(new Uri(context.Options.Model.EndpointUri, "/api/tags"), cancellationToken).ConfigureAwait(false);
        if (body is null) { return []; }
        return [Contains(body, model)
            ? CheckResult.Pass(Id, $"Model {model} is installed.")
            : CheckResult.Warn(Id, $"Model {model} is not installed; it will be pulled.", $"Run 'ollama pull {model}' or start with --pull (the download is several GB).")];
    }

    public static bool Contains(string tagsJson, string model)
    {
        var wanted = model.Contains(':', StringComparison.Ordinal) ? model : model + ":latest";
        try
        {
            using var document = JsonDocument.Parse(tagsJson);
            return document.RootElement.TryGetProperty("models", out var models)
                && models.ValueKind == JsonValueKind.Array
                && models.EnumerateArray().Any(item => item.TryGetProperty("name", out var name) && string.Equals(name.GetString(), wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class GpuCheck : IPreflightCheck
{
    public const string Id = "gpu";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var result = await context.Environment.RunAsync("nvidia-smi", ["--query-gpu=name,memory.total", "--format=csv,noheader"], cancellationToken).ConfigureAwait(false);
        var first = result.ExitCode == 0 ? result.StdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() : null;
        return [first is null
            ? CheckResult.Warn(Id, "No NVIDIA GPU detected; CPU mode, expect slow answers.", "Install a current NVIDIA driver, or accept CPU mode (qwen3.5:2b is faster: --model qwen3.5:2b).")
            : CheckResult.Pass(Id, $"NVIDIA GPU: {first}.")];
    }
}

public sealed class DotnetSdkCheck : IPreflightCheck
{
    public const string Id = "dotnet-sdk";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var result = await context.Environment.RunAsync("dotnet", ["--version"], cancellationToken).ConfigureAwait(false);
        var version = result.ExitCode == 0 ? result.StdoutText.Trim() : null;
        return [string.IsNullOrEmpty(version)
            ? CheckResult.Fail(Id, ".NET SDK was not found.", "Install the .NET SDK version in global.json from https://dotnet.microsoft.com/download.")
            : CheckResult.Pass(Id, $".NET SDK {version}.")];
    }
}

public sealed class FlutterCheck : IPreflightCheck
{
    public const string Id = "flutter";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var result = await context.Environment.RunAsync("flutter", ["--version"], cancellationToken).ConfigureAwait(false);
        return [result.ExitCode == 0
            ? CheckResult.Pass(Id, "Flutter SDK found.")
            : CheckResult.Warn(Id, "Flutter SDK not found (optional); the desktop shell will be skipped.", "Install Flutter for the shell, or use headless chat via the API.")];
    }
}
