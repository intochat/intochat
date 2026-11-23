using IntoChat.Aspire.Verification;
using IntoChat.Aspire;

namespace IntoChat.OS.Tests.Integration.Components.Aspire;

internal sealed class FakeVerifier : ICandidateVerifier
{
    public bool Passed { get; set; } = true;

    public Task<VerificationReport> Verify(string brainId, string sha, CandidateEndpoints endpoints, VerificationPlan plan, CancellationToken ct) =>
        Task.FromResult(new VerificationReport([new VerificationCheck("fake", Passed, Passed ? "ok" : "failed")], TimeSpan.Zero));
}
