namespace IntoChat.Aspire.Candidates;

public interface IAcaJobTokenProvider
{
    Task<string> GetTokenAsync(string armScope, CancellationToken ct);
}
