using System.Reflection;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Support.Fakes;

/// <summary>
/// Journey test helpers on <see cref="Person"/> for befriending people and binding installed screens.
/// </summary>
public static class PersonJourneyExtensions
{
    /// <summary>Establishes a mutual friendship between <paramref name="person"/> and <paramref name="other"/> via <see cref="IFriends"/>.</summary>
    public static async Task Befriend(this Person person, Person other, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(other);
        ct.ThrowIfCancellationRequested();

        var code = await person.Get<IFriends>(FriendsDefaults.Id(person.Brain.Value)).Mint().ConfigureAwait(false);
        await other.Get<IFriends>(FriendsDefaults.Id(other.Brain.Value)).Redeem(code).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes typed slot <paramref name="bindings"/> for <typeparamref name="TScreen"/> through <see cref="IInstalls"/>
    /// and deactivates any active activations of <typeparamref name="TScreen"/> in <paramref name="person"/>'s brain so
    /// the next <see cref="Person.Open{TScreen}"/> activates with the new bindings.
    /// </summary>
    public static async Task Install<TScreen>(this Person person, CancellationToken ct, params Binding[] bindings)
        where TScreen : class
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(bindings);
        ct.ThrowIfCancellationRequested();

        var alias = ResolveComponentAlias(typeof(TScreen));
        var installs = person.Get<IInstalls>(new NeuronId(person.Brain, "synapse.installs", "default"));
        await installs.BindComponent(new ComponentId(alias), bindings, ct).ConfigureAwait(false);
        await person.DeactivateScreens<TScreen>().ConfigureAwait(false);
    }

    /// <summary>
    /// Writes typed slot <paramref name="bindings"/> for <typeparamref name="TScreen"/> through <see cref="IInstalls"/>
    /// and deactivates any active activations of <typeparamref name="TScreen"/> in <paramref name="person"/>'s brain.
    /// </summary>
    public static Task Install<TScreen>(this Person person, params Binding[] bindings)
        where TScreen : class
        => Install<TScreen>(person, CancellationToken.None, bindings);

    private static string ResolveComponentAlias(Type componentType)
        => PlainNeuronContracts.TypeOf(componentType)
            ?? componentType.GetCustomAttribute<NeuronAttribute>()?.Type
            ?? componentType.Name;
}
