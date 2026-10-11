namespace DigitalBrain.RiskSeal;

internal interface IRiskSealProvider
{
    Task<RiskSealReply> ScoreAsync(Uri baseUrl, string apiKey, ScoreRequest request, CancellationToken cancellationToken);
    Task<RiskSealReply> ReadAccountAsync(Uri baseUrl, string apiKey, CancellationToken cancellationToken);
}

// Status is null when nothing answered (timeout or network); Failure is the provider's status, never a secret.
internal sealed record RiskSealReply(int? Status, string Body)
{
    public bool Succeeded => Status is >= 200 and < 300;

    public string Failure => Status switch
    {
        401 => "unauthorized (401)",
        403 => "forbidden (403)",
        null => Body,
        { } code => $"{code}",
    };
}
