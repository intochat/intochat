namespace DigitalBrain.Revisions;

internal sealed class RevisionVerifier : IRevisionVerifier
{
    public Task<VerificationReport> Verify(string brainId, string mcpEndpoint, VerificationPlan plan, CancellationToken ct)
        => throw new NotSupportedException("The verification driver arrives with the next change.");
}