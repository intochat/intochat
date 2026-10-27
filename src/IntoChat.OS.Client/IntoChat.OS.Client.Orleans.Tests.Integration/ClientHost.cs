[assembly: AssemblyFixture(typeof(IntoChat.OS.Client.Orleans.Tests.Integration.ClientHost))]

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

// The shared host, with the facts' plain emitters and registry.
public sealed class ClientHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithNeuronModule<EmitterNeurons>();
}
