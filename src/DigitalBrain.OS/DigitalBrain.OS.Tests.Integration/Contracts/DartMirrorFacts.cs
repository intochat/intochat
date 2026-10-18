using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalBrain.OS.Tests.Integration.Contracts;

// The Flutter shell mirrors C# wire records by hand. A key renamed on either side fails here, not in a running app.
public sealed class DartMirrorFacts(OsHost host)
{
    // The live envelope's keys, pinned by the Flutter module's golden of LiveSignalEndpoints.Envelope.
    private static HashSet<string> GoldenEnvelopeKeys()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(SolutionProjects.DirectoryOf("DigitalBrain.Modules.Flutter.Tests.Integration"), "Contracts", "live-envelope.golden.json")));
        return [.. golden.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    [Fact]
    public void TheDartLiveEnvelopeMirrorsTheServersEnvelope()
    {
        var envelope = DartSource.Class(DartSource.Core(Path.Combine("models", "cluster.dart")), "LiveEnvelope");
        var golden = GoldenEnvelopeKeys();

        Assert.Equal(golden.Order(), DartSource.FinalFields(envelope).Order());
        Assert.Equal(golden.Order(), DartSource.JsonKeys(envelope, "json").Order());
    }

    // The shell reads a session from /identity/session and from the member that register and login answer.
    [Fact]
    public async Task TheDartSessionReadsKeysTheIdentityEndpointsAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var read = DartSource.JsonKeys(DartSource.Member(DartSource.Core("ui_client.dart"), "void _useSession("), "session");
        Assert.NotEmpty(read);
        using var ada = await host.PersonAsync("ada", ct);

        var session = await ada.Http.GetFromJsonAsync<JsonElement>("/identity/session", ct);
        using var login = await ada.Http.PostAsJsonAsync("/identity/login", new { principalId = ada.PrincipalId, password = ada.PrincipalId + "-password-123" }, ct);
        login.EnsureSuccessStatusCode();
        var member = await login.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Subset(Keys(session), read);
        Assert.Subset(Keys(member), read);
    }

    private static HashSet<string> Keys(JsonElement json) => [.. json.EnumerateObject().Select(property => property.Name)];
}
