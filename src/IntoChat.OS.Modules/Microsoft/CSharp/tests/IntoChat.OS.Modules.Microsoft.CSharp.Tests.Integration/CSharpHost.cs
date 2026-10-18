using IntoChat.OS.Microsoft.CSharp;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration.CSharpHost))]

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// The shared host composes the C# module alone. Facts that drive a sandbox session through their own
// FakeSessionHost (RuntimeBrain), replace a service, turn authoring off, add modules or restart keep their own
// TestBrain ([Trait("host", "own")]); most of them share the "acc-brain" runtime scope, so they cannot share a host.
public sealed class CSharpHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<CSharpModule>();
}
