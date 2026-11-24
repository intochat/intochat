using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.Google.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Drive;

[GenerateSerializer, Alias("db.google-drive.state")]
internal sealed record DriveState : ConnectionState;

// One Google Drive as storage. Locations are Drive file ids; "root" is My Drive.
// Google documents are exported as plain text; other Google formats are not text. storage@1 lives in DriveStorage.cs (05-09).
[DigitalBrain.Neurons.Neuron(DriveScopes.Connector, State = "google-drive")]
internal sealed partial class DriveNeuron(
    IOperations operations, IConfirmations? confirmations = null, IStorageWriteAuthorizer? authorizer = null)
    : ConnectionNeuron<DriveState>
{
    private const string Root = "root";
    private const string RootName = "My Drive";
    private const string FolderType = "application/vnd.google-apps.folder";
    private const string DocumentType = "application/vnd.google-apps.document";
    private const int MaxDepth = 32;

    protected override async Task<OAuthGrant> AcceptGrantAsync(OAuthGrant grant)
    {
        using var about = await AboutAsync(grant.AccessToken, CancellationToken.None);
        if (!about.IsSuccessStatusCode) { throw new DriveUnavailableException("Google did not verify the Drive account. Reconnect Google Drive."); }
        using var json = JsonDocument.Parse(await about.Content.ReadAsStringAsync());
        var account = json.RootElement.TryGetProperty("user", out var user) && user.TryGetProperty("emailAddress", out var email) ? email.GetString() : null;
        if (string.IsNullOrWhiteSpace(account)) { throw new DriveUnavailableException("Google did not verify the Drive account. Reconnect Google Drive."); }
        await SaveQuietly(Snapshot with { Account = account });
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

    private Task<HttpResponseMessage> AboutAsync(string accessToken, CancellationToken ct) =>
        RequestAsync(new Uri(Options.Endpoint, "about?fields=user(emailAddress)"), GoogleDriveOperations.About, accessToken, ct);

    private async Task<HttpResponseMessage> RequestAsync(Uri uri, string operation, string accessToken, CancellationToken ct, RangeHeaderValue? range = null,
        HttpMethod? method = null, HttpContent? content = null)
    {
        using var trace = EnterTrace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var client = ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleHttp.ClientName);
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Range = range;
        request.SetExternalCall(new(operation, Funding.OwnConnection, new(Neuron: FullKey), Guid.NewGuid().ToString("N")));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
    }

    private IDisposable? EnterTrace()
    {
        var caller = Context.Caller ?? throw new UnauthorizedAccessException("The Drive connection is called only with a stamped caller.");
        if (caller.Brain.Value != Key.Brain)
        { throw new UnauthorizedAccessException("The Drive connection belongs to another brain."); }
        if (TraceContext.Current is not null) { return null; }
        var payer = caller.For?.Account ?? throw new UnauthorizedAccessException("Drive calls outside a trace are paid by a person.");
        return TraceContext.Enter(TraceContext.Begin(payer, Key.Brain, TraceContext.OriginOf(Key.Brain, FullKey)));
    }

    private DriveModuleOptions Options => ServiceProvider.GetRequiredService<DriveModuleOptions>();
}
