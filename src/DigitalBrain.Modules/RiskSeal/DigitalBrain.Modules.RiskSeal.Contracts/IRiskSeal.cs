using DigitalBrain;
using Orleans.Concurrency;

namespace DigitalBrain.RiskSeal;

// Fraud and digital-footprint scoring, keyed {brain}/connection/riskseal/{id}: an api-key connection
// scores with the key a user pasted, a deployment connection with the deployment's RiskSeal integration.
[Alias("riskseal")]
[Orleans.Metadata.DefaultGrainType("riskseal")]
public interface IRiskSeal : INeuron
{
    Task<RiskScore> Score(ScoreRequest request);

    [ReadOnly]
    Task<RiskScore[]> ReadRecent(int limit);
}

// At least one of Email or Phone; Phone is E.164.
[GenerateSerializer, Alias("riskseal.score-request")]
public sealed record ScoreRequest(
    [property: Id(0)] string? Email,
    [property: Id(1)] string? Phone,
    [property: Id(2)] string? IpAddress = null,
    [property: Id(3)] string? DocumentNumber = null);

[GenerateSerializer, Alias("riskseal.score")]
public sealed record RiskScore(
    [property: Id(0)] string RequestId,
    [property: Id(1)] DateTimeOffset ScoredAt,
    [property: Id(2)] int? Score,
    [property: Id(3)] string? RiskLevel,
    [property: Id(4)] string RawJson,
    [property: Id(5)] RiskSignal[] Signals)
{
    public const int MaxRawBytes = 32 * 1024;
}

[GenerateSerializer, Alias("riskseal.signal")]
public sealed record RiskSignal([property: Id(0)] string Name, [property: Id(1)] string Value);

[GenerateSerializer, Alias("riskseal.unavailable")]
public sealed class RiskSealUnavailableException(string message) : InvalidOperationException(message);
