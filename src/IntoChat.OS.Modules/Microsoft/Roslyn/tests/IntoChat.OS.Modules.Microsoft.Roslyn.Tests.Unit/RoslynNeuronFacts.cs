using DigitalBrain;
using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

// The plain workspace neuron (06-06a), a cluster-scoped singleton keyed by the workspace key. The Unit tier runs each fact on TestOS.
public sealed class RoslynNeuronFacts(RoslynFixture fixture)
{
    private static readonly BrainId FactsBrain = new("facts");

    [Fact]
    public async Task AWorkspaceNothingOpenedReadsAsNotOpenedAtGenerationZero()
    {
        var runtime = fixture.Runtime;
        var roslyn = runtime.Get<IRoslyn>(new NeuronId(FactsBrain, "microsoft.roslyn", "fresh-testos"));

        var snapshot = await roslyn.Read();

        Assert.Equal((WorkspacePhase.NotOpened, 0L, null), (snapshot.Phase, snapshot.Generation, snapshot.SolutionPath));
    }

    [Fact]
    public async Task ABlankSolutionPathAndAReloadOfNothingAreRefused()
    {
        var runtime = fixture.Runtime;
        var roslyn = runtime.Get<IRoslyn>(new NeuronId(FactsBrain, "microsoft.roslyn", "refusals-testos"));

        await Assert.ThrowsAsync<ArgumentException>(() => roslyn.Open(new OpenWorkspace(" ")));
        await Assert.ThrowsAsync<InvalidOperationException>(roslyn.Reload);
    }
}
