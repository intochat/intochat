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

        Assert.Equal("IntoChat.OS.Contracts", aiContracts.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.AI", aiImpl.GetName().Name);

        Assert.Equal(aiContracts, typeof(ITestVerificationNeuron).Assembly);
        Assert.Equal(aiImpl, typeof(TestVerificationNeuron).Assembly);

        var appsImpl = typeof(AppsModule).Assembly;
        Assert.Equal("IntoChat.OS.Modules.AI.Testing", typeof(ITestVerificationService).Namespace);
        Assert.Equal("IntoChat.OS.Modules.AI.Testing", typeof(ITestVerificationNeuron).Namespace);
        Assert.DoesNotContain(appsImpl.GetTypes(), t => t.Name is "ITestVerificationService" or "TestVerificationService");
    }
}
