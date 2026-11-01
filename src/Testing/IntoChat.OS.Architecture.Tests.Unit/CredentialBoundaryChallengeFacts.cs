using System.Reflection;
using System.Reflection.Emit;
using DigitalBrain.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Xunit;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class CredentialBoundaryChallengeFacts
{
    private static readonly MethodInfo DirectlyConstructsCallerMethod = typeof(CredentialBoundaryFacts)
        .GetMethod("DirectlyConstructsCaller", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo CallsMethod = typeof(CredentialBoundaryFacts)
        .GetMethod("Calls", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void ConstructorAnalyzerDetectsDirectCapabilityCallerConstruction()
    {
        // Find public constructors of CapabilityCaller
        var ctors = typeof(CapabilityCaller).GetConstructors();
        Assert.NotEmpty(ctors);

        // Every constructor that is not the copy constructor must be flagged as direct construction
        foreach (var ctor in ctors)
        {
            var isCopyCtor = ctor.GetParameters().Length == 1 && ctor.GetParameters()[0].ParameterType == typeof(CapabilityCaller);
            var isDirect = (bool)DirectlyConstructsCallerMethod.Invoke(null, [ctor])!;

            if (isCopyCtor)
            {
                Assert.False(isDirect, "Copy constructor used by 'with' expressions must not be flagged.");
            }
            else
            {
                Assert.True(isDirect, $"Direct constructor {ctor} must be flagged by DirectlyConstructsCaller.");
            }
        }
    }

    [Fact]
    public void ILAnalyzerCatchesNewobjCapabilityCallerInMethod()
    {
        // Method that calls new CapabilityCaller(...) directly
        var method = typeof(CredentialBoundaryChallengeFacts).GetMethod(nameof(SampleMethodWithDirectConstruction), BindingFlags.NonPublic | BindingFlags.Static)!;
        var called = ((IEnumerable<MethodBase>)CallsMethod.Invoke(null, [method])!).ToArray();

        var caught = called.Any(m => (bool)DirectlyConstructsCallerMethod.Invoke(null, [m])!);
        Assert.True(caught, "IL analysis must detect direct construction of CapabilityCaller.");
    }

    [Fact]
    public void ILAnalyzerPermitsRecordWithExpressionCopyConstructor()
    {
        // Method that uses 'with' expression (copy constructor)
        var method = typeof(CredentialBoundaryChallengeFacts).GetMethod(nameof(SampleMethodWithRecordWithExpression), BindingFlags.NonPublic | BindingFlags.Static)!;
        var called = ((IEnumerable<MethodBase>)CallsMethod.Invoke(null, [method])!).ToArray();

        var caught = called.Any(m => (bool)DirectlyConstructsCallerMethod.Invoke(null, [m])!);
        Assert.False(caught, "IL analysis must NOT flag record 'with' expressions.");
    }

    [Fact]
    public void SolutionContainsZeroDirectCapabilityCallerConstructorsInProductionAssemblies()
    {
        // Directly execute the architecture assertion
        var facts = new CredentialBoundaryFacts();
        facts.NoProductionMethodDirectlyConstructsCapabilityCallerOutsideFactory();
        facts.NoModuleConstructsACallerContext();
        facts.OnlyApprovedSitesConstructCallerContext();
    }

    private static CapabilityCaller SampleMethodWithDirectConstruction()
    {
        return new CapabilityCaller("user1", "acc1", "brain1", "app", null);
    }

    private static CapabilityCaller SampleMethodWithRecordWithExpression(CapabilityCaller existing)
    {
        return existing with { AppId = "new-app" };
    }
}
