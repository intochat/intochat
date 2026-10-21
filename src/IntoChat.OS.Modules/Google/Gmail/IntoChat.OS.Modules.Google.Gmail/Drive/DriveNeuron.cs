using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Google.Gmail;

[GenerateSerializer, Alias("db.google-drive.state")]
internal sealed class DriveState : ConnectionState;

// One Google Drive as storage, read only. Locations are Drive file ids; "root" is My Drive.
// Google documents are exported as plain text; other Google formats are not text.
[GrainType(DriveScopes.Connector)]
internal sealed class DriveNeuron(
    [PersistentState("google-drive", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<DriveState> store)
    : ConnectionNeuron<DriveState>(store), IStorage
{
    private const string Root = "root";
    private const string RootName = "My Drive";
    private const string FolderType = "application/vnd.google-apps.folder";
    private const string DocumentType = "application/vnd.google-apps.document";
    private const int MaxDepth = 32;

    public Task<StorageEntry[]> Roots(CancellationToken cancellationToken = default) =>
        Task.FromResult<StorageEntry[]>([new(Root, RootName, true)]);

    public async Task<StorageEntry[]> List(string location, CancellationToken cancellationToken = default)
    {
        var folder = string.IsNullOrEmpty(location) ? Root : location;
        var query = Uri.EscapeDataString($"'{folder.Replace("\\", "\\\\").Replace("'", "\\'")}' in parents and trashed = false");
        using var json = await GetJsonAsync($"files?q={query}&fields=files(id,name,mimeType)&orderBy=folder,name&pageSize=200", cancellationToken);
        return [.. json.RootElement.GetProperty("files").EnumerateArray().Select(Entry)];
    }

    public async Task<string> Read(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        using var metadata = await GetJsonAsync($"files/{Uri.EscapeDataString(location)}?fields=id,name,mimeType", cancellationToken);
        var type = metadata.RootElement.GetProperty("mimeType").GetString();
        var path = type switch
        {
            DocumentType => $"files/{Uri.EscapeDataString(location)}/export?mimeType=text%2Fplain",
            FolderType => throw new InvalidOperationException("A Drive folder is not a text file."),
            { } google when google.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)
                => throw new InvalidOperationException("This Google file has no text form."),
            _ => $"files/{Uri.EscapeDataString(location)}?alt=media",
        };
        return await SendAsync(path, cancellationToken, response => response.Content.ReadAsStringAsync(cancellationToken));
    }

    // Walks parents up to My Drive; a file shared with the user ends the chain at its topmost visible folder.
    public async Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(location)) { return []; }
        var chain = new List<StorageEntry>();
        var current = location;
        for (var depth = 0; depth < MaxDepth && current != Root; depth++)
        {
            using var json = await GetJsonAsync($"files/{Uri.EscapeDataString(current)}?fields=id,name,mimeType,parents", cancellationToken);
            var file = json.RootElement;
            chain.Add(Entry(file));
            // My Drive (Google names it so) and the topmost folder shared with the user have no visible parent.
            if (!file.TryGetProperty("parents", out var parents) || parents.GetArrayLength() == 0)
            {
                chain.Reverse();
                return [.. chain];
            }
            current = parents[0].GetString()!;
        }
        chain.Add(new(Root, RootName, true));
        chain.Reverse();
        return [.. chain];
    }

    protected override async Task<OAuthGrant> AcceptGrantAsync(OAuthGrant grant)
    {
        using var about = await AboutAsync(grant.AccessToken, CancellationToken.None);
        if (!about.IsSuccessStatusCode) { throw new GmailUnavailableException("Google did not verify the Drive account. Reconnect Google Drive."); }
        using var json = JsonDocument.Parse(await about.Content.ReadAsStringAsync());
        Snapshot.Account = json.RootElement.TryGetProperty("user", out var user) && user.TryGetProperty("emailAddress", out var email) ? email.GetString() : null;
        if (string.IsNullOrWhiteSpace(Snapshot.Account)) { throw new GmailUnavailableException("Google did not verify the Drive account. Reconnect Google Drive."); }
        return grant;
    }

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        using var about = await AboutAsync(credential.Value, ct);
        return about.StatusCode switch
        {
            HttpStatusCode.OK => ConnectionProbeResult.Connected(),
            HttpStatusCode.Unauthorized => ConnectionProbeResult.Expired("The Google Drive access token was refused."),
            var status => ConnectionProbeResult.Failing($"Google answered the Drive check with HTTP {(int)status}."),
        };
    }

    private static StorageEntry Entry(JsonElement file) =>
        new(file.GetProperty("id").GetString()!, file.GetProperty("name").GetString() ?? "", file.GetProperty("mimeType").GetString() == FolderType);

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct) =>
        await SendAsync(path, ct, async response => await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct));

    private Task<T> SendAsync<T>(string path, CancellationToken ct, Func<HttpResponseMessage, Task<T>> read) =>
        UseCredentialAsync(async (credential, token) =>
        {
            using var response = await RequestAsync(new Uri(Options.DriveEndpoint, path), credential.Value, token);
            if (response.StatusCode == HttpStatusCode.NotFound) { throw new FileNotFoundException("This Drive item does not exist or is not shared with this account."); }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await MarkExpired("The Google Drive access token was refused. Reconnect Google Drive.");
                throw new UnauthorizedAccessException("Google refused the Drive access token. Reconnect Google Drive.");
            }
            if (!response.IsSuccessStatusCode) { throw new GmailUnavailableException($"Google Drive answered HTTP {(int)response.StatusCode}."); }
            return await read(response);
        }, ct);

    private Task<HttpResponseMessage> AboutAsync(string accessToken, CancellationToken ct) =>
        RequestAsync(new Uri(Options.DriveEndpoint, "about?fields=user(emailAddress)"), accessToken, ct);

    private async Task<HttpResponseMessage> RequestAsync(Uri uri, string accessToken, CancellationToken ct)
    {
        using var client = ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(GmailModule.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, ct);
    }

    private GmailModuleOptions Options => ServiceProvider.GetRequiredService<GmailModuleOptions>();
}

internal static class DriveScopes
{
    internal const string Connector = "google-drive";
    internal const string Read = "https://www.googleapis.com/auth/drive.readonly";
}
