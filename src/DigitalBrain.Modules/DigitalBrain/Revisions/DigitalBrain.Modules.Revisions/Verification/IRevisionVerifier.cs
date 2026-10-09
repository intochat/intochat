namespace DigitalBrain.Revisions;

public interface IRevisionVerifier
{
    Task<VerificationReport> Verify(string brainId, string mcpEndpoint, VerificationPlan plan, CancellationToken ct);
}