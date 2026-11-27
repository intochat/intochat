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

internal sealed record CreateIssuePayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body,
    [property: JsonPropertyName("labels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Labels,
    [property: JsonPropertyName("assignees"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Assignees);

internal sealed record GitHubMcpConnectPayload(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("token")] string Token);

internal sealed record IssueRevisionPayload(
    [property: JsonPropertyName("RepositoryId")] long RepositoryId,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("isOpen")] bool IsOpen,
    [property: JsonPropertyName("labels")] string[] Labels,
    [property: JsonPropertyName("commentCount")] int CommentCount,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt);

[JsonSerializable(typeof(PullRequestRevisionPayload))]
[JsonSerializable(typeof(PullRequestCiRevisionPayload))]
[JsonSerializable(typeof(CheckEvidencePayload))]
[JsonSerializable(typeof(CreateIssuePayload))]
[JsonSerializable(typeof(GitHubMcpConnectPayload))]
[JsonSerializable(typeof(IssueRevisionPayload))]
internal sealed partial class GitHubAotJsonContext : JsonSerializerContext;
