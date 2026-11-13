using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps;
using Orleans.Runtime;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class TriRepoCreatorBoundaryFacts
{
    [Fact]
    public void CapabilityCallerPrimitivesResideExclusivelyInDigitalBrain()
    {
        var digitalBrain = typeof(DigitalBrain.Neurons.Neuron<>).Assembly;
        Assert.Equal(digitalBrain, typeof(CapabilityCaller).Assembly);
        Assert.Equal(digitalBrain, typeof(CallerKind).Assembly);
        Assert.Equal(digitalBrain, typeof(TrustedEdge).Assembly);
        Assert.Equal(digitalBrain, typeof(CapabilityCallers).Assembly);
        Assert.Equal(digitalBrain, typeof(AppPrincipal).Assembly);

        var framework = typeof(RuntimeCallerContext).Assembly;
        Assert.DoesNotContain(framework.GetTypes(), t => t.Name is "CallerKind" or "TrustedEdge" or "CapabilityCallers" or "AppPrincipal");
    }

    [Fact]
    public void TestVerificationPrimitivesResideInAiModulesAndNotInApps()
    {
        var aiContracts = typeof(ITestVerificationService).Assembly;
        var aiImpl = typeof(TestVerificationService).Assembly;

        Assert.Equal("IntoChat.OS.Modules.AI", aiContracts.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.AI", aiImpl.GetName().Name);

        Assert.Equal(aiContracts, typeof(ITestVerificationNeuron).Assembly);
        Assert.Equal(aiImpl, typeof(TestVerificationNeuron).Assembly);

        var appsContracts = typeof(IApp).Assembly;
        var appsImpl = typeof(AppsModule).Assembly;
        Assert.DoesNotContain(appsContracts.GetTypes(), t => t.Name is "ITestVerificationService");
        Assert.DoesNotContain(appsImpl.GetTypes(), t => t.Name is "TestVerificationService");
    }

    [Fact]
    public void CallerContextStamperStampScopeRestoresAndClearsUnstampedFlows()
    {
        RequestContext.Remove(RuntimeCallerContext.RequestContextKey);
        if (true)
        {
            Assert.False(RuntimeCallerContext.TryGet(out _));

            var ownerStamp = CapabilityCallers.Of("owner1", "acc1", "brain1");
            using (RuntimeCallerContext.StampScope(ownerStamp))
            {
                Assert.True(RuntimeCallerContext.TryGet(out var active));
                Assert.Equal("owner1", active!.PrincipalId);

                var nestedStamp = CapabilityCallers.Of("app:calc", "acc1", "brain1", CallerKind.App, "calc");
                using (RuntimeCallerContext.StampScope(nestedStamp))
                {
                    Assert.True(RuntimeCallerContext.TryGet(out var nested));
                    Assert.Equal(CallerKind.App, nested!.Kind);
                }

                Assert.True(RuntimeCallerContext.TryGet(out var restored));
                Assert.Equal("owner1", restored!.PrincipalId);
                Assert.Equal(CallerKind.User, restored.Kind);
            }

            Assert.False(RuntimeCallerContext.TryGet(out _));
        }
    }
}
