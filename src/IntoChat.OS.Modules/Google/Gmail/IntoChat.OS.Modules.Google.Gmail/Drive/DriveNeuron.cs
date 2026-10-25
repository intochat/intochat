using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Integrations;
using IntoChat.OS.Framework.Modules.Metering;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Gmail;

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
        using var json = await GetJsonAsync($"files?q={query}&fields=files(id,name,mimeType)&orderBy=folder,name&pageSize=200", GoogleOperations.DriveList, cancellationToken);
        return [.. json.RootElement.GetProperty("files").EnumerateArray().Select(Entry)];
    }

    public async Task<string> Read(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        using var metadata = await GetJsonAsync($"files/{Uri.EscapeDataString(location)}?fields=id,name,mimeType", GoogleOperations.DriveMetadata, cancellationToken);
        var type = metadata.RootElement.GetProperty("mimeType").GetString();
        var path = type switch
        {
            DocumentType => $"files/{Uri.EscapeDataString(location)}/export?mimeType=text%2Fplain",
            FolderType => throw new InvalidOperationException("A Drive folder is not a text file."),
            { } google when google.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)
                => throw new InvalidOperationException("This Google file has no text form."),
            _ => $"files/{Uri.EscapeDataString(location)}?alt=media",
        };
        return await SendAsync(path, type == DocumentType ? GoogleOperations.DriveExport : GoogleOperations.DriveMedia,
            cancellationToken, response => response.Content.ReadAsStringAsync(cancellationToken));
    }

    // Walks parents up to My Drive; a file shared with the user ends the chain at its topmost visible folder.
    public async Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(location)) { return []; }
        var chain = new List<StorageEntry>();
        var current = location;
        for (var depth = 0; depth < MaxDepth && current != Root; depth++)
        {
            using var json = await GetJsonAsync($"files/{Uri.EscapeDataString(current)}?fields=id,name,mimeType,parents", GoogleOperations.DriveMetadata, cancellationToken);
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

    private async Task<JsonDocument> GetJsonAsync(string path, string operation, CancellationToken ct) =>
        await SendAsync(path, operation, ct, async response => await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct));

    private async Task<T> SendAsync<T>(string path, string operation, CancellationToken ct, Func<HttpResponseMessage, Task<T>> read)
    {
        using var trace = EnterTrace();
        return await UseCredentialAsync(async (credential, token) =>
        {
            using var response = await RequestAsync(new Uri(Options.DriveEndpoint, path), operation, credential.Value, token);
            if (response.StatusCode == HttpStatusCode.NotFound) { throw new FileNotFoundException("This Drive item does not exist or is not shared with this account."); }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await MarkExpired("The Google Drive access token was refused. Reconnect Google Drive.");
                throw new UnauthorizedAccessException("Google refused the Drive access token. Reconnect Google Drive.");
            }
            if (!response.IsSuccessStatusCode) { throw new GmailUnavailableException($"Google Drive answered HTTP {(int)response.StatusCode}."); }
            return await read(response);
        }, ct);
    }

    private Task<HttpResponseMessage> AboutAsync(string accessToken, CancellationToken ct) =>
        RequestAsync(new Uri(Options.DriveEndpoint, "about?fields=user(emailAddress)"), GoogleOperations.DriveAbout, accessToken, ct);

    private async Task<HttpResponseMessage> RequestAsync(Uri uri, string operation, string accessToken, CancellationToken ct)
    {
        using var trace = EnterTrace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var client = ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(GmailModule.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.SetExternalCall(new(operation, Funding.OwnConnection, new(Neuron: this.GetPrimaryKeyString()), Guid.NewGuid().ToString("N")));
        return await client.SendAsync(request, linked.Token);
    }

    private IDisposable? EnterTrace()
    {
        var caller = CallerContextStamper.Require();
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != Key.Brain)
        { throw new UnauthorizedAccessException("The Drive connection belongs to another brain."); }
        return TraceContext.Current is null
            ? TraceContext.Enter(TraceContext.Begin(caller.AccountId, Key.Brain, new NeuronId(this.GetPrimaryKeyString()))) : null;
    }

    private GmailModuleOptions Options => ServiceProvider.GetRequiredService<GmailModuleOptions>();
}

internal static class DriveScopes
{
    internal const string Connector = "google-drive";
    internal const string Read = "https://www.googleapis.com/auth/drive.readonly";
}
