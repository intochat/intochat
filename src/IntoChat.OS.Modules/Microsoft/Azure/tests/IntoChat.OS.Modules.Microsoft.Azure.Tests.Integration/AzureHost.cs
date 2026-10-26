[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Microsoft.Azure.Tests.Integration.AzureHost))]

namespace IntoChat.OS.Modules.Microsoft.Azure.Tests.Integration;

// The shared in-process host for the assembly; the Azure neuron composes over a recording ARM in the unit tier, so this tier
// only needs the module present on the silo.
public sealed class AzureHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;
}
