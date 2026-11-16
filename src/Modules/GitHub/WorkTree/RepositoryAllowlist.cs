using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// Manages the allowlist of local Git repositories configured via <see cref="IConfiguration"/>,
/// <c>repos.json</c>, or programmatic host registration.
/// </summary>
internal sealed class RepositoryAllowlist(
    IConfiguration? configuration,
    WorkTreeOptions options,
    ILogger logger)
{
    private readonly ConcurrentDictionary<string, RegisteredRepository> _programmatic = new(StringComparer.OrdinalIgnoreCase);

    public string HomeDirectory => ResolveHomeDirectory();

    public string ReposJsonPath => !string.IsNullOrWhiteSpace(options.ReposConfigPath)
        ? Path.GetFullPath(options.ReposConfigPath)
        : Path.Combine(HomeDirectory, "repos.json");

    public void Register(RegisteredRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.LocalPath);

        var id = RepositoryId.Parse(repository.Id);
        var normalized = repository with
        {
            Id = id.Value,
            LocalPath = Path.GetFullPath(repository.LocalPath.Trim()),
        };
        _programmatic[normalized.Id] = normalized;
    }

    public IReadOnlyList<RegisteredRepository> List()
    {
        var merged = new Dictionary<string, RegisteredRepository>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in LoadFromConfiguration())
        {
            merged[entry.Id] = entry;
        }

        foreach (var entry in LoadFromReposJson())
        {
            merged[entry.Id] = entry;
        }

        foreach (var (key, entry) in _programmatic)
        {
            merged[key] = entry;
        }

        return [.. merged.Values.OrderBy(static r => r.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public RegisteredRepository Resolve(RepositoryId repositoryId)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        var wanted = repositoryId.Value;
        var all = List();

        var direct = all.FirstOrDefault(r => string.Equals(r.Id, wanted, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        var bySlug = all.FirstOrDefault(r =>
            (!string.IsNullOrWhiteSpace(r.GitHubRepo) && string.Equals(r.GitHubRepo, wanted, StringComparison.OrdinalIgnoreCase))
            || (wanted.Contains('/', StringComparison.Ordinal)
                && string.Equals(r.Id, wanted[(wanted.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase)));
        if (bySlug is not null)
        {
            return bySlug;
        }

        var known = all.Count == 0 ? "none configured" : string.Join(", ", all.Select(static r => r.Id));
        throw new UnauthorizedAccessException(
            $"Repository '{wanted}' is not registered in the allowlist ({ReposJsonPath}). Known repositories: {known}.");
    }

    private string ResolveHomeDirectory()
    {
        if (!string.IsNullOrWhiteSpace(options.HomeDirectory))
        {
            return Path.GetFullPath(options.HomeDirectory);
        }

        var fromConfig = configuration?["IntoChatOS:Home"] ?? configuration?["Home"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return Path.GetFullPath(fromConfig);
        }

        var fromEnv = Environment.GetEnvironmentVariable("IntoChatOS__Home");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(profile, ".intochat"));
    }

    private IEnumerable<RegisteredRepository> LoadFromConfiguration()
    {
        if (configuration is null)
        {
            yield break;
        }

        foreach (var sectionName in new[] { "IntoChatOS:Repositories", "GitHub:Repositories", "Repositories" })
        {
            var section = configuration.GetSection(sectionName);
            foreach (var child in section.GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(child.Value))
                {
                    yield return new RegisteredRepository(child.Key, Path.GetFullPath(child.Value));
                }
                else if (child["path"] is { Length: > 0 } || child["localPath"] is { Length: > 0 })
                {
                    var local = child["path"] ?? child["localPath"]!;
                    yield return new RegisteredRepository(
                        child["id"] ?? child.Key,
                        Path.GetFullPath(local),
                        child["githubRepo"] ?? child["remote"],
                        child["baseBranch"] ?? child["defaultBaseBranch"]);
                }
            }
        }
    }

    private IEnumerable<RegisteredRepository> LoadFromReposJson()
    {
        var path = ReposJsonPath;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ParseReposJsonRoot(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not parse repository allowlist at {Path}", path);
            return [];
        }
    }

    internal static IReadOnlyList<RegisteredRepository> ParseReposJsonRoot(JsonElement root)
    {
        var list = new List<RegisteredRepository>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (TryParseRepoObject(null, item) is { } repo)
                {
                    list.Add(repo);
                }
            }

            return list;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("repositories", out var reposProp))
            {
                return ParseReposJsonRoot(reposProp);
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } strPath)
                {
                    list.Add(new RegisteredRepository(prop.Name, Path.GetFullPath(strPath)));
                }
                else if (TryParseRepoObject(prop.Name, prop.Value) is { } repo)
                {
                    list.Add(repo);
                }
            }
        }

        return list;
    }

    private static RegisteredRepository? TryParseRepoObject(string? fallbackId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetStringIgnoreCase(element, "id") ?? GetStringIgnoreCase(element, "name") ?? fallbackId;
        var localPath = GetStringIgnoreCase(element, "path") ?? GetStringIgnoreCase(element, "localPath");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        var githubRepo = GetStringIgnoreCase(element, "githubRepo") ?? GetStringIgnoreCase(element, "repo") ?? GetStringIgnoreCase(element, "slug");
        var baseBranch = GetStringIgnoreCase(element, "baseBranch") ?? GetStringIgnoreCase(element, "defaultBaseBranch");
        return new RegisteredRepository(id.Trim(), Path.GetFullPath(localPath.Trim()), githubRepo?.Trim(), baseBranch?.Trim());
    }

    private static string? GetStringIgnoreCase(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }
}
