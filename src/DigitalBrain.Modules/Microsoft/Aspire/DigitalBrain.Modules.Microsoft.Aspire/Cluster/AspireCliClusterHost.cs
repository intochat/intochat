using System.Text.Json;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Aspire.Candidates;

namespace DigitalBrain.Microsoft.Aspire.Cluster;

// The live AppHost through the `aspire` CLI (13.6 has no `aspire mcp start`): `describe`, `resource restart`, `otel spans`.
// The AppHost project is found from the repository, Workdir's parent.
internal sealed class AspireCliClusterHost : IClusterHost
{
    public const string SiloResource = "OperatingSystem";
    public const string McpResource = "digitalbrain-mcp";
    // `otel spans` caps its result; a verification run's spans must not fall off that window.
    private const string SpanLimit = "10000";
    private const string AppHostProject = "src/DigitalBrain.AppHost/DigitalBrain.AppHost.csproj";

    private readonly IProcessRunner processes;
    private readonly Func<CancellationToken, Task<string>> workdir;

    public AspireCliClusterHost(IModuleIntegrations<AspireModule> integrations, IProcessRunner processes)
        : this(processes, ct => Workdir(integrations, ct))
    {
    }

    internal AspireCliClusterHost(IProcessRunner processes, Func<CancellationToken, Task<string>> workdir)
    {
        this.processes = processes;
        this.workdir = workdir;
    }

    public async Task<string> CurrentSha(CancellationToken ct) => LiveBuildFile.Read(await workdir(ct))?.Sha ?? string.Empty;

    public async Task SetLive(string sha, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        LiveBuildFile.Write(await workdir(ct), sha);
    }

    public async Task<SiloView[]> Silos(CancellationToken ct)
    {
        var sha = await CurrentSha(ct);
        using var json = JsonDocument.Parse(JsonPart(await Aspire(["describe", "--format", "Json"], ct)));
        return [.. Resources(json.RootElement)
            .Where(resource => Text(resource, "displayName", "name") is { } name && name.StartsWith(SiloResource, StringComparison.Ordinal))
            .Select(resource => new SiloView(Text(resource, "name", "displayName")!, sha, Text(resource, "state") ?? "Unknown", Text(resource, "healthStatus", "health")))
            .OrderBy(silo => silo.Name, StringComparer.Ordinal)];
    }

    // The MCP server is an Orleans client of the same build: both restart onto live.json's build. This call
    // runs inside the silo, so the silo restarts last: its restart ends the caller.
    public async Task RestartSilos(CancellationToken ct)
    {
        await Aspire(["resource", McpResource, "restart"], ct);
        await Aspire(["resource", SiloResource, "restart"], ct);
    }

    // `otel spans` lists one JSON object per span; `source` is the exporting service's name (its OTEL service.name).
    public async Task<int> Spans(string service, string span, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(JsonPart(await Aspire(["otel", "spans", "--format", "Json", "--search", span, "--limit", SpanLimit], ct)));
        return json.RootElement.ValueKind != JsonValueKind.Array ? 0
            : json.RootElement.EnumerateArray().Count(item => Text(item, "name") == span && From(Text(item, "source"), service));
    }

    // A replica's service name carries a suffix: candidate-{sha}-mcp is still candidate-{sha}'s.
    private static bool From(string? source, string service)
        => source is not null && (source == service || source.StartsWith(service + "-", StringComparison.Ordinal));

    internal static IEnumerable<JsonElement> Resources(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Array => root.EnumerateArray(),
        JsonValueKind.Object when root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array => resources.EnumerateArray(),
        _ => [],
    };

    private static string? Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) { return value.GetString(); }
        }
        return null;
    }

    // The CLI may print log lines before its JSON: the document starts at the first line that opens one.
    internal static string JsonPart(string output)
    {
        var lines = output.Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimStart().StartsWith('[') || line.TrimStart().StartsWith('{'));
        return start < 0
            ? throw new InvalidOperationException($"aspire printed no JSON: {output}")
            : string.Join('\n', lines[start..]);
    }

    private async Task<string> Aspire(string[] command, CancellationToken ct)
    {
        var repository = Path.GetDirectoryName(Path.GetFullPath(await workdir(ct)))!;
        string[] args = [.. command, "--apphost", Path.Combine(repository, AppHostProject), "--non-interactive", "--nologo"];
        var result = await processes.RunAsync("aspire", args, repository, null, ct);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException($"aspire {string.Join(' ', command)} exited {result.ExitCode}: {result.Error}{result.Output}");
    }

    private static async Task<string> Workdir(IModuleIntegrations<AspireModule> integrations, CancellationToken ct)
    {
        var snapshot = await integrations.Get("aspire").ReadAsync(ct);
        return snapshot.Settings.TryGetValue("Workdir", out var workdir) && workdir is { Length: > 0 }
            ? Path.GetFullPath(workdir)
            : throw new InvalidOperationException("the aspire Integration has no Workdir");
    }
}
