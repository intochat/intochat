using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Access;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class TriRepoCreatorBoundaryFacts
{
    [Fact]
    public void CallerPrimitivesResideExclusivelyInDigitalBrain()
    {
        var digitalBrain = typeof(DigitalBrain.Neurons.Neuron<>).Assembly;
        Assert.Equal(digitalBrain, typeof(Caller).Assembly);
        Assert.Equal(digitalBrain, typeof(Actor).Assembly);
        Assert.Equal(digitalBrain, typeof(EdgeAuthority).Assembly);
        Assert.Equal(digitalBrain, typeof(AppPrincipal).Assembly);
        Assert.DoesNotContain(typeof(CallerApps).Assembly.GetTypes(), t => t.Name is "Caller" or "Actor" or "EdgeAuthority" or "AppPrincipal");
    }

    [Fact]
    public void TestVerificationPrimitivesResideInAiModulesAndNotInApps()
    {
        var aiContracts = typeof(ITestVerificationService).Assembly;
        var aiImpl = typeof(TestVerificationService).Assembly;

        Assert.Equal("IntoChat.OS.Modules.AI.Contracts", aiContracts.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.AI", aiImpl.GetName().Name);

        Assert.Equal(aiContracts, typeof(ITestVerificationNeuron).Assembly);
        Assert.Equal(aiImpl, typeof(TestVerificationNeuron).Assembly);

        var appsContracts = typeof(IApp).Assembly;
        var appsImpl = typeof(AppsModule).Assembly;
        Assert.DoesNotContain(appsContracts.GetTypes(), t => t.Name is "ITestVerificationService");
        Assert.DoesNotContain(appsImpl.GetTypes(), t => t.Name is "TestVerificationService");
    }
}
