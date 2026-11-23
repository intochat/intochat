using DigitalBrain.Runtime.Http.Surface;
using IntoChat.Aspire;
using IntoChat.OS.Kernel.Composition.Kernel;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

// Stored agent definitions and transcripts name Aspire contracts by the full names they had in the Azure module
// (IntoChat.OS.Modules.Microsoft.Aspire.*). After the move to IntoChat.Aspire those names must still resolve.
public sealed class AspireFormerNamesFacts
{
    private const string Former = "IntoChat.OS.Modules.Microsoft.Aspire.";

    public static TheoryData<string, Type> Contracts() => new()
    {
        { Former + "IAspire", typeof(IAspire) },
        { Former + "ICandidate", typeof(ICandidate) },
        { Former + "ICandidates", typeof(ICandidates) },
        { Former + "IDeployment", typeof(IDeployment) },
    };

    [Theory]
    [MemberData(nameof(Contracts))]
    public void TheKernelResolvesAFormerFullName(string former, Type current)
        => Assert.Same(current, KernelInvocationSink.ContractOf(former));

    [Theory]
    [MemberData(nameof(Contracts))]
    public void LoadedContractsMapAFormerFullNameToTheCurrentOne(string former, Type current)
    {
        Assert.Same(current, LoadedContracts.Find(former, ["IntoChat.OS", "IntoChat.Aspire"]));
        Assert.Equal(current.FullName, LoadedContracts.CurrentName(former, ["IntoChat.OS", "IntoChat.Aspire"]));
    }

    [Fact]
    public void TheInternalDeploymentDriverResolvesByItsFormerFullName()
    {
        var driver = typeof(AspireModule).Assembly.GetType("IntoChat.Aspire.Deployment.IDeploymentDriver", throwOnError: true)!;
        Assert.Same(driver, KernelInvocationSink.ContractOf("IntoChat.OS.Modules.Microsoft.Aspire.Deployment.IDeploymentDriver"));
    }

    [Fact]
    public void TheAliasesAreUnchanged()
    {
        Assert.Same(typeof(IAspire), KernelInvocationSink.ContractOf("aspire"));
        Assert.Same(typeof(ICandidate), KernelInvocationSink.ContractOf("aspire.candidate"));
        Assert.Same(typeof(ICandidates), KernelInvocationSink.ContractOf("aspire.candidates"));
        Assert.Same(typeof(IDeployment), KernelInvocationSink.ContractOf("aspire.deployment"));
    }
}
