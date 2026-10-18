[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration.OrleansHost))]

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;

// The shared host is the plain in-process brain, which serves ICluster as the former per-fact host did; facts read the cluster
// from a fresh person's brain.
public sealed class OrleansHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;
}
