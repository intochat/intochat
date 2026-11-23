using DigitalBrain;
using DigitalBrain.Components;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Whether a compiled app's required host capabilities (neuron types) are available in the current runtime.
/// </summary>
public abstract record AppAvailability
{
    /// <summary>Every required slot's neuron type is hosted (or no host check was configured).</summary>
    public sealed record Ready : AppAvailability
    {
        public static readonly Ready Instance = new();
    }

    /// <summary>One or more required slots target neuron types that are not hosted in this runtime.</summary>
    public sealed record NeedsProvisioning(IReadOnlyList<string> MissingNeuronTypes) : AppAvailability;

    /// <summary>Evaluates whether <paramref name="appType"/> can activate on a host described by <paramref name="isNeuronTypeHosted"/>.</summary>
    public static AppAvailability Evaluate(
        Type appType,
        Func<string, bool>? isNeuronTypeHosted,
        Func<Type, bool>? isContractHosted = null)
    {
        ArgumentNullException.ThrowIfNull(appType);
        if (isNeuronTypeHosted is null || !ComponentManifests.HasSlots(appType))
        {
            return Ready.Instance;
        }

        List<string>? missing = null;
        foreach (var need in ComponentManifests.SlotsOf(appType))
        {
            if (!need.Required) { continue; }
            if (need.Contract.IsDefined(typeof(CapabilityAttribute), inherit: false)) { continue; }
            var neuronType = Slots.NeuronTypeOf(need.Contract);
            if (string.IsNullOrWhiteSpace(neuronType)
                || Slots.IsTargetHosted(need.Contract, neuronType, isNeuronTypeHosted, isContractHosted))
            {
                continue;
            }
            missing ??= [];
            if (!missing.Contains(neuronType, StringComparer.Ordinal))
            {
                missing.Add(neuronType);
            }
        }

        return missing is { Count: > 0 }
            ? new NeedsProvisioning([.. missing])
            : Ready.Instance;
    }
}
