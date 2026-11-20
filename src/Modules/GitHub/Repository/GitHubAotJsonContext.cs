using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

internal sealed record PullRequestRevisionPayload(
    [property: JsonPropertyName("RepositoryId")] long RepositoryId,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("merge")] string? Merge,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("draft")] bool Draft);

internal sealed record PullRequestCiRevisionPayload(
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("ciSha")] string CiSha,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("checks")] IReadOnlyList<GitHubCheck> Checks);

internal sealed record CheckSuiteEvidenceItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("conclusion")] string? Conclusion);

internal sealed record CheckEvidencePayload(
    [property: JsonPropertyName("checks")] GitHubCheck[] Checks,
    [property: JsonPropertyName("suites")] CheckSuiteEvidenceItem[] Suites);

[JsonSerializable(typeof(PullRequestRevisionPayload))]
[JsonSerializable(typeof(PullRequestCiRevisionPayload))]
[JsonSerializable(typeof(CheckEvidencePayload))]
internal sealed partial class GitHubAotJsonContext : JsonSerializerContext;
