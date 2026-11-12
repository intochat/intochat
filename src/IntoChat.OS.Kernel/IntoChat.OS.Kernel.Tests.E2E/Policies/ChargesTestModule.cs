using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Limits;
using DigitalBrain.Modules;
using DigitalBrain.Rules;

namespace IntoChat.OS.Kernel.Tests.E2E.Policies;

[DigitalBrain.Modules.Module("charges")]
public sealed class ChargesTestModule : DigitalBrain.Modules.Module
{
    public static readonly ActionDescriptor Charges = new("charges.charge", "charges", "USD");

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: [])
    {
        Actions = [Charges],
    };
}

public sealed record ChargeCount(int Count = 0)
{
    public ChargeCount() : this(0)
    {
    }
}

[NeuronAlias("charges"), DefaultNeuronType("charges")]
public interface ICharges
{
    static string Key(string brain) => $"{brain}/charges/default";

    Task<int> Charge(decimal amount, CancellationToken ct = default);
}

[DigitalBrain.Neurons.Neuron("charges")]
public sealed class ChargesNeuron : DigitalBrain.Neurons.Neuron<ChargeCount>, ICharges
{
    public async Task<int> Charge(decimal amount, CancellationToken ct = default)
    {
        var decision = await Context.Get<IPolicies>(NeuronId.Parse(IPolicies.Key(Id.Brain.Value)))
            .Allow("charges.charge", amount, null, ct);
        PolicyGuard.Require(decision);
        var next = State.Count + 1;
        await Save(new ChargeCount(next), [new StateChanged(Id.ToString(), "charged")]);
        return next;
    }
}
