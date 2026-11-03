using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain;
using DigitalBrain.Capabilities;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Tasks;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps;
using Orleans.Runtime;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class TriRepoCreatorBoundaryFacts
{
    [Fact]
    public void CapabilityCallerPrimitivesResideExclusivelyInDigitalBrain()
    {
        var digitalBrain = typeof(DigitalBrain.Neuron<>).Assembly;
        Assert.Equal(digitalBrain, typeof(CapabilityCaller).Assembly);
        Assert.Equal(digitalBrain, typeof(CallerKind).Assembly);
        Assert.Equal(digitalBrain, typeof(TrustedEdge).Assembly);
        Assert.Equal(digitalBrain, typeof(CapabilityCallers).Assembly);
        Assert.Equal(digitalBrain, typeof(AppPrincipal).Assembly);

        var framework = typeof(RuntimeCallerContext).Assembly;
        Assert.DoesNotContain(framework.GetTypes(), t => t.Name is "CallerKind" or "TrustedEdge" or "CapabilityCallers" or "AppPrincipal");
    }

    [Fact]
    public void TaskAndTestVerificationPrimitivesResideInAiModulesAndNotInApps()
    {
        var aiContracts = typeof(ITaskEngine).Assembly;
        var aiImpl = typeof(TaskEngine).Assembly;

        Assert.Equal("IntoChat.OS.Modules.AI.Contracts", aiContracts.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.AI", aiImpl.GetName().Name);

        Assert.Equal(aiContracts, typeof(ITaskNeuron).Assembly);
        Assert.Equal(aiContracts, typeof(ITaskDirectoryNeuron).Assembly);
        Assert.Equal(aiContracts, typeof(ITestVerificationService).Assembly);
        Assert.Equal(aiContracts, typeof(ITestVerificationNeuron).Assembly);

        Assert.Equal(aiImpl, typeof(TaskNeuron).Assembly);
        Assert.Equal(aiImpl, typeof(TaskDirectoryNeuron).Assembly);
        Assert.Equal(aiImpl, typeof(TestVerificationService).Assembly);
        Assert.Equal(aiImpl, typeof(TestVerificationNeuron).Assembly);

        var appsContracts = typeof(IApp).Assembly;
        var appsImpl = typeof(AppsModule).Assembly;
        Assert.DoesNotContain(appsContracts.GetTypes(), t => t.Name is "ITaskEngine" or "ITestVerificationService");
        Assert.DoesNotContain(appsImpl.GetTypes(), t => t.Name is "TaskEngine" or "TestVerificationService");
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

    [Fact]
    public void CreatorAppCompileInMemoryDetectsSemanticErrorsAndAcceptsValidApps()
    {
        var invalidSemanticSource = """
            public sealed class BrokenApp
            {
                public void Run() => NonExistentSymbolXYZ.Execute();
            }
            """;
        var (invalidSuccess, invalidDiagnostics) = CreatorApp.CompileInMemory(invalidSemanticSource);
        Assert.False(invalidSuccess);
        Assert.NotEmpty(invalidDiagnostics);
        Assert.Contains(invalidDiagnostics, d => d.Contains("CS0103", StringComparison.Ordinal));

        var validSource = """
            public sealed record SampleState(int Count);
            public sealed class ValidSandboxCheck
            {
                public Task<SampleState> StepAsync(SampleState s) => Task.FromResult(s with { Count = s.Count + 1 });
            }
            """;
        var (validSuccess, validDiagnostics) = CreatorApp.CompileInMemory(validSource);
        Assert.True(validSuccess, string.Join("; ", validDiagnostics));
        Assert.Empty(validDiagnostics);
    }
}
