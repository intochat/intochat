using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Rules;

namespace IntoChat.OS.Kernel.Tests.E2E.Policies;

[DigitalBrain.Module("charges")]
public sealed class ChargesTestModule : DigitalBrain.Module
{
    public static readonly ActionDescriptor Charges = new("charges.charge", "charges", "USD");

    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
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

[DigitalBrain.Neuron("charges")]
public sealed class ChargesNeuron : DigitalBrain.Neuron<ChargeCount>, ICharges
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
