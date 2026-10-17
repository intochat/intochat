using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.RiskSeal.Signals;
using Orleans.Concurrency;

namespace DigitalBrain.RiskSeal;

// Serves only connection keys {brain}/connection/riskseal/{id}. An api-key connection scores with the
// key sealed in its state; a deployment connection scores with the deployment's RiskSeal integration.
[GrainType("riskseal")]
internal sealed partial class RiskSealNeuron(
    IRiskSealProvider provider, IModuleIntegrations<RiskSealModule> integrations, RiskSealModuleOptions options, TimeProvider time,
    [PersistentState("riskseal", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<RiskSealState> store)
    : ConnectionNeuron<RiskSealState>(store), IRiskSeal
{
    private const string NotConfigured = "RiskSeal is not configured";

    public async Task<RiskScore> Score(ScoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(request.Phone))
        { throw new ArgumentException("A RiskSeal score needs an email or a phone number.", nameof(request)); }
        if (!string.IsNullOrWhiteSpace(request.Phone) && !E164().IsMatch(request.Phone))
        { throw new ArgumentException("The phone number must be E.164, like +51987654321.", nameof(request)); }

        var requestId = Guid.NewGuid().ToString("n");
        RequireOwnBrain();
        RiskSealReply? reply;
        try
        {
            reply = await UseCredentialAsync(async (credential, ct) => await TargetAsync(credential, ct) is { } target
                ? await provider.ScoreAsync(target.BaseUrl, target.ApiKey, request, ct) : null, CancellationToken.None);
        }
        catch (InvalidOperationException error) when (error is not RiskSealUnavailableException)
        { throw new RiskSealUnavailableException("This RiskSeal connection is not connected."); }
        if (reply is null)
        {
            await Save(Snapshot, new RiskScoreFailed(requestId, NotConfigured));
            throw new RiskSealUnavailableException(NotConfigured);
        }
        if (!reply.Succeeded)
        {
            await Save(Snapshot, new RiskScoreFailed(requestId, reply.Failure));
            throw new RiskSealUnavailableException($"RiskSeal refused the score: {reply.Failure}.");
        }

        var score = Parse(requestId, time.GetUtcNow(), reply.Body);
        Snapshot.Scores = [.. Snapshot.Scores.Append(score).TakeLast(RiskSealState.MaxScores)];
        await Save(Snapshot, new RiskScored(score));
        return score;
    }

    [ReadOnly]
    public Task<RiskScore[]> ReadRecent(int limit)
        => Task.FromResult(limit < 1 ? [] : Snapshot.Scores.TakeLast(Math.Min(limit, RiskSealState.MaxScores)).Reverse().ToArray());

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        if (await TargetAsync(credential, ct) is not { } target) { return ConnectionProbeResult.Failing(NotConfigured); }
        var reply = await provider.ReadAccountAsync(target.BaseUrl, target.ApiKey, ct);
        return reply.Succeeded ? ConnectionProbeResult.Connected() : ConnectionProbeResult.Failing(reply.Failure);
    }

    // The deployment method's sealed value is only a marker; its key is the integration's. Null when that
    // integration is not Ready.
    private async Task<(Uri BaseUrl, string ApiKey)?> TargetAsync(Credential credential, CancellationToken ct)
    {
        var integration = integrations.Get(RiskSealModule.Integration.Id);
        var snapshot = await integration.ReadAsync(ct);
        var baseUrl = options.Resolve(snapshot.Settings.TryGetValue("BaseUrl", out var setting) ? setting : null);
        if (Snapshot.AuthMethodId != RiskSealModule.DeploymentMethod) { return (baseUrl, credential.Value); }
        if (snapshot.Status != IntegrationStatus.Ready) { return null; }
        return (baseUrl, await integration.ReadSecretAsync("ApiKey", ct));
    }

    private void RequireOwnBrain()
    {
        (string Brain, string Connector, string Connection) key;
        try { key = Key; }
        catch (ArgumentException) { throw new UnauthorizedAccessException("A canonical connection key is required."); }
        if (key.Brain != BrainScope.CurrentId()) { throw new UnauthorizedAccessException("The connection belongs to another brain."); }
    }

    // ASSUMED payload: {"request_id","score","risk_level","signals":[{"name","value"}] or {"name": value}}.
    internal static RiskScore Parse(string fallbackId, DateTimeOffset scoredAt, string body)
    {
        var raw = Truncate(body);
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return new(fallbackId, scoredAt, null, null, raw, []); }
            var id = Text(root, "request_id") ?? Text(root, "requestId") ?? fallbackId;
            int? score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var value)
                ? (int)Math.Round(value) : null;
            var level = Text(root, "risk_level") ?? Text(root, "riskLevel");
            RiskSignal[] signals = !root.TryGetProperty("signals", out var list) ? []
                : list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object && Text(e, "name") is not null)
                        .Select(e => new RiskSignal(Text(e, "name")!, e.TryGetProperty("value", out var v) ? Value(v) : ""))]
                : list.ValueKind == JsonValueKind.Object
                    ? [.. list.EnumerateObject().Select(p => new RiskSignal(p.Name, Value(p.Value)))]
                : [];
            return new(id, scoredAt, score, level, raw, signals);
        }
        catch (JsonException) { return new(fallbackId, scoredAt, null, null, raw, []); }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Value(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static string Truncate(string body)
    {
        if (Encoding.UTF8.GetByteCount(body) <= RiskScore.MaxRawBytes) { return body; }
        var bytes = Encoding.UTF8.GetBytes(body);
        var length = RiskScore.MaxRawBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) { length--; }
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$")]
    private static partial Regex E164();
}
