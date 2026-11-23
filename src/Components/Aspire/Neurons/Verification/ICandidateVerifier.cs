namespace IntoChat.Aspire.Verification;

internal interface ICandidateVerifier
{
    Task<VerificationReport> Verify(string brainId, string sha, CandidateEndpoints endpoints, VerificationPlan plan, CancellationToken ct);
}
