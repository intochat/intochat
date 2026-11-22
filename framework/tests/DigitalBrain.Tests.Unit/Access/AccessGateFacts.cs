using DigitalBrain.Access;
using DigitalBrain.Kernel;
using DigitalBrain.Modules;

namespace DigitalBrain.Tests.Unit.Access;

public sealed class AccessGateFacts
{
    private static readonly BrainId Alice = new("alice");
    private static readonly NeuronId Target = new(Alice, "wallet", "default");

    public interface IPublicContract
    {
        Task Touch();
    }

    internal interface IInternalContract
    {
        Task Touch();
    }

    [Theory]
    [InlineData(typeof(IPublicContract))]
    [InlineData(typeof(IInternalContract))]
    public async Task AdmitRefusesACallWithNoCaller(Type contract)
    {
        var gate = new AccessGate(new FixedAmbient(null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await gate.Admit(Target, Describe(contract), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdmitReturnsTheStampedCaller()
    {
        var caller = EdgeAuthority.CreateInternal().Person("alice-user", "acct_1", Alice);
        var gate = new AccessGate(new FixedAmbient(caller));

        Assert.Same(caller, await gate.Admit(Target, Describe(typeof(IPublicContract)), TestContext.Current.CancellationToken));
    }

    private static CallDescriptor Describe(Type contract)
        => new("Touch", 0, typeof(Task), static (_, _) => Task.FromResult<object?>(null))
        {
            Contract = contract,
            Method = contract.GetMethod("Touch")!,
        };

    private sealed class FixedAmbient(Caller? current) : ICallerAmbient
    {
        public Caller? Current { get; } = current;
    }
}
