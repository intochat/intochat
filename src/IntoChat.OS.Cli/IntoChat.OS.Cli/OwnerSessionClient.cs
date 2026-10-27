using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntoChat.OS.Cli;

internal sealed class OwnerSessionClient(HttpClient http)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal string Brain { get; private set; } = "";
    internal string Route => "brains/" + Uri.EscapeDataString(Brain) + "/apps/packages";
    internal async Task Connect(string? brain, CancellationToken ct)
    {
        using var response = await http.GetAsync("identity/session", ct);
        if (response.StatusCode == HttpStatusCode.NoContent) { throw new UnauthorizedAccessException("No owner session. Supply INTOCHAT_SESSION_COOKIE or INTOCHAT_BEARER_TOKEN intentionally, or use the loopback-open desktop."); }
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<Session>(Json, ct) ?? throw new UnauthorizedAccessException("Missing owner session.");
        if (string.IsNullOrWhiteSpace(session.PrincipalId) || string.IsNullOrWhiteSpace(session.AccountId) || string.IsNullOrWhiteSpace(session.BrainId))
        { throw new UnauthorizedAccessException("Ambiguous owner session."); }
        var account = session.AccountId;
        var principal = session.PrincipalId;
        // A requested destination must be selected through the real identity endpoint, never inferred from a URL.
        if (brain is not null && brain != session.BrainId)
        {
            var brains = await http.GetFromJsonAsync<BrainView[]>("identity/brains", Json, ct) ?? [];
            if (brains.Count(x => x.Id == brain) != 1) { throw new InvalidOperationException("The requested brain is missing or ambiguous."); }
            await Post<JsonElement>("identity/session/brain", new { session.AccountId, BrainId = brain }, ct);
            using var verify = await http.GetAsync("identity/session", ct);
            verify.EnsureSuccessStatusCode();
            session = await verify.Content.ReadFromJsonAsync<Session>(Json, ct) ?? throw new UnauthorizedAccessException("Missing selected session.");
            if (session.BrainId != brain || session.AccountId != account || session.PrincipalId != principal) { throw new UnauthorizedAccessException("Destination selection was not confirmed."); }
        }
        Brain = session.BrainId;
    }
    internal async Task<Upload> Stage(Stream archive, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await archive.CopyToAsync(buffer, ct);
        using var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new("application/octet-stream");
        using var response = await http.PostAsync(Route, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Upload>(Json, ct) ?? throw new InvalidDataException("Missing upload receipt.");
    }
    internal async Task<T> Post<T>(string route, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(route, body, Json, ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent) { return default!; }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("Missing server response.");
    }
    internal sealed record Session(string PrincipalId, string AccountId, string BrainId, string Role);
    private sealed record BrainView(string Id, string Kind);
    internal sealed record Requirements(string[]? SemanticTypes = null, string[]? Connectors = null, string[]? Secrets = null, string[]? Capabilities = null)
    {
        internal string Consent()
        {
            static string Join(string[]? value) => string.Join(",", (value ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            var text = "s:" + Join(SemanticTypes) + "\nc:" + Join(Connectors) + "\nx:" + Join(Secrets) + (Capabilities is { Length: > 0 } ? "\nk:" + Join(Capabilities) : "");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }
    internal sealed record Upload(string Digest, string Id, string Name, string Version, Requirements Requires, string[] HostAdapters, string TrustMessage);
    internal sealed record Review(string Token, string Digest, string Brain, string Name, long ExpectedRevision, bool ReplacesExisting, DateTimeOffset ExpiresAt, Requirements Requires, string[] HostAdapters);
    internal sealed record Generation(string Digest, long Revision, Guid OperationId);
    internal sealed record Snapshot(Generation? ActiveCompiled);
    internal sealed record Lease(string Token, string Brain, string Name, string Project, DateTimeOffset ExpiresAt);
}
