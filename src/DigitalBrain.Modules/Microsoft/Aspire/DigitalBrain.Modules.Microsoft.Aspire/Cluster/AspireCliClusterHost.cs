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
    private readonly string? self;

    // Aspire names each instance's telemetry after its resource: OTEL_SERVICE_NAME is this silo's instance name.
    public AspireCliClusterHost(IModuleIntegrations<AspireModule> integrations, IProcessRunner processes)
        : this(processes, ct => Workdir(integrations, ct), Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"))
    {
    }

    internal AspireCliClusterHost(IProcessRunner processes, Func<CancellationToken, Task<string>> workdir, string? self = null)
    {
        this.processes = processes;
        this.workdir = workdir;
        this.self = self;
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
        return [.. (await Describe(ct))
            .Where(resource => Instance(Text(resource, "displayName", "name"), SiloResource))
            .Select(resource => new SiloView(Text(resource, "displayName", "name")!, sha, Text(resource, "state") ?? "Unknown", Text(resource, "healthStatus", "health")))
            .OrderBy(silo => silo.Name, StringComparer.Ordinal)];
    }

    // The MCP servers are Orleans clients of the same build: they restart onto live.json's build first, then the
    // silos. This call runs inside one silo, so that silo restarts last: its restart ends the caller. Stopping
    // the silo signals its console, and a CLI sharing that console cancels the restart between stop and start
    // (seen live): the caller's own restart is issued detached and is not awaited.
    public async Task RestartSilos(CancellationToken ct)
    {
        var names = (await Describe(ct)).Select(resource => Text(resource, "displayName", "name")).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
        foreach (var mcp in names.Where(name => Instance(name, McpResource)))
        {
            await Aspire(["resource", mcp, "restart"], ct);
        }
        var silos = names.Where(name => Instance(name, SiloResource)).ToArray();
        var own = self is not null && silos.Contains(self) ? self : null;
        foreach (var silo in silos.Where(name => name != own))
        {
            await Aspire(["resource", silo, "restart"], ct);
        }
        if (own is not null)
        {
            var repository = await Repository(ct);
            processes.Detach("aspire", Arguments(repository, ["resource", own, "restart"]), repository);
        }
    }

    // The AppHost runs each role as named instances {role}-{x}, x a single letter (AspireModuleHosting.Instances);
    // {role}-front and the rebuilders are not instances.
    internal static bool Instance(string? name, string role)
        => name is not null && name.Length == role.Length + 2 && name.StartsWith(role + "-", StringComparison.Ordinal) && char.IsAsciiLetterLower(name[^1]);

    private async Task<JsonElement[]> Describe(CancellationToken ct)
    {
        using var json = JsonDocument.Parse(JsonPart(await Aspire(["describe", "--format", "Json"], ct)));
        return [.. Resources(json.RootElement).Select(resource => resource.Clone())];
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
        var repository = await Repository(ct);
        var result = await processes.RunAsync("aspire", Arguments(repository, command), repository, null, ct);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException($"aspire {string.Join(' ', command)} exited {result.ExitCode}: {result.Error}{result.Output}");
    }

    private async Task<string> Repository(CancellationToken ct) => Path.GetDirectoryName(Path.GetFullPath(await workdir(ct)))!;

    private static string[] Arguments(string repository, string[] command)
        => [.. command, "--apphost", Path.Combine(repository, AppHostProject), "--non-interactive", "--nologo"];

    private static async Task<string> Workdir(IModuleIntegrations<AspireModule> integrations, CancellationToken ct)
    {
        var snapshot = await integrations.Get("aspire").ReadAsync(ct);
        return snapshot.Settings.TryGetValue("Workdir", out var workdir) && workdir is { Length: > 0 }
            ? Path.GetFullPath(workdir)
            : throw new InvalidOperationException("the aspire Integration has no Workdir");
    }
}
