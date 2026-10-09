using DigitalBrain.Revisions;

namespace DigitalBrain.Modules.Revisions.Tests;

internal sealed class FakeVerifier : IRevisionVerifier
{
    public Func<VerificationPlan, CancellationToken, Task<VerificationReport>> Answer { get; set; }
        = (_, _) => Task.FromResult(new VerificationReport([new("context", true, "")], TimeSpan.Zero));

    public Task<VerificationReport> Verify(string brainId, string mcpEndpoint, VerificationPlan plan, CancellationToken ct) => Answer(plan, ct);
}