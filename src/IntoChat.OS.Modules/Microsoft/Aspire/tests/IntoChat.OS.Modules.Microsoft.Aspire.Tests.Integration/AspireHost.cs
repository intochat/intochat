[assembly: AssemblyFixture(typeof(IntoChat.OS.Microsoft.Aspire.Tests.AspireHost))]

namespace IntoChat.OS.Microsoft.Aspire.Tests;

// The shared host composes Aspire, Mcp and Tools over an idle fake cluster: the candidate verifier facts drive the
// verifier service as the host's owner against their own scripted MCP server, in sequence within their class. Facts
// that script the candidate host, the cluster, the verifier, the membership feed or the sampling period keep their
// own TestBrain ([Trait("host", "own")]); those are host-global services.
public sealed class AspireHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => CandidateVerifierFacts.Compose(brain, new FakeClusterHost());
}
