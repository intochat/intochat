using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Sequencer;

// R127: a machine registers a P-256 agreement key beside its signing key; circle part keys are wrapped to it.
public sealed class MachineAgreementKeyFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AMachineRegistersAnAgreementKeyBesideItsSigningKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = brain.Services.GetRequiredService<INodeDirectoryClient>();
        using var machine = SequencerFacts.Machine();

        await directory.RegisterMachine(machine.MachineId, machine.PublicKey, machine.AgreementKey, "alice", [],
            machine.Sign(Signing.Registration(machine.MachineId, machine.PublicKey, machine.AgreementKey, "alice", [])));

        var known = await directory.Machine(machine.MachineId);
        Assert.NotNull(known);
        Assert.Equal(machine.PublicKey, known.PublicKey);
        Assert.Equal(machine.AgreementKey, known.AgreementKey);
        Assert.NotEqual(known.PublicKey, known.AgreementKey);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARegistrationWithoutAnAgreementKeyIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = brain.Services.GetRequiredService<INodeDirectoryClient>();
        using var machine = SequencerFacts.Machine();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.RegisterMachine(machine.MachineId, machine.PublicKey, [], "alice", [],
            machine.Sign(Signing.Registration(machine.MachineId, machine.PublicKey, [], "alice", []))));
        Assert.Null(await directory.Machine(machine.MachineId));
    }
}
