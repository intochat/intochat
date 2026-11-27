using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Components;
using DigitalBrain.Neurons;

namespace DigitalBrain.Kernel;

/// <summary>
/// Declarative reactive subscription rule derived from <see cref="ReactsAttribute"/> or
/// <see cref="ComponentManifest.Reacts"/> on a hosted neuron class.
/// </summary>
internal sealed record DeclarativeReactRule(
    string SubscriberType,
    Type? SourceType,
    Type SignalType,
    string SignalAlias);

/// <summary>
/// Runtime index of declarative <c>[Reacts]</c> component subscriptions. Allows signal fanout
/// and reconciliation to wake never-activated component neurons on their first matching signal,
/// while enforcing source-type matching and self-loop prevention.
/// </summary>
internal sealed class DeclarativeReactsIndex
{
    internal const string DefaultKey = "default";

    private readonly ConcurrentDictionary<string, DeclarativeReactRule[]> _bySubscriber = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DeclarativeReactRule[] _all = [];

    /// <summary>Registers any declarative <c>[Reacts]</c> rules declared on <paramref name="neuronClass"/>.</summary>
    public void Register(string neuronType, Type neuronClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(neuronType);
        ArgumentNullException.ThrowIfNull(neuronClass);

        // Stateless neurons cannot hold synapse cursors.
        if (neuronClass.IsDefined(typeof(StatelessNeuronAttribute), inherit: false))
        {
            return;
        }

        var rules = ExtractRules(neuronType, neuronClass);
        if (rules.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _bySubscriber[neuronType] = rules;
            _all = [.. _bySubscriber.Values.SelectMany(static r => r)];
        }
    }

    /// <summary>Registers all hosted neuron classes from <paramref name="registrations"/>.</summary>
    public void RegisterRange(IEnumerable<(string NeuronType, Type NeuronClass)> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var (neuronType, neuronClass) in registrations)
        {
            Register(neuronType, neuronClass);
        }
    }

    /// <summary>Whether <paramref name="subscriber"/> is a declarative <c>[Reacts]</c> target.</summary>
    public bool IsDeclarativeSubscriber(NeuronId subscriber)
        => string.Equals(subscriber.Key, DefaultKey, StringComparison.Ordinal)
           && _bySubscriber.ContainsKey(subscriber.Type);

    /// <summary>All distinct signal aliases declared for <paramref name="subscriber"/>.</summary>
    public IReadOnlyList<string> AliasesOf(NeuronId subscriber)
    {
        if (!string.Equals(subscriber.Key, DefaultKey, StringComparison.Ordinal)
            || !_bySubscriber.TryGetValue(subscriber.Type, out var rules))
        {
            return [];
        }

        return [.. rules.Select(static r => r.SignalAlias).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Returns whether <paramref name="subscriber"/> accepts a brain-wide signal from
    /// <paramref name="publisher"/> with <paramref name="alias"/> under its declarative rules.
    /// </summary>
    public bool Accepts(
        NeuronId subscriber,
        NeuronId publisher,
        string alias,
        Func<string, Type?> neuronClassOf,
        Func<string, Type?> signalTypeOf)
    {
        if (publisher == subscriber)
        {
            return false;
        }

        if (!string.Equals(subscriber.Key, DefaultKey, StringComparison.Ordinal)
            || !_bySubscriber.TryGetValue(subscriber.Type, out var rules))
        {
            return false;
        }

        var publisherClass = neuronClassOf(publisher.Type);
        var signalType = signalTypeOf(alias);
        foreach (var rule in rules)
        {
            if (Matches(rule, publisherClass, alias, signalType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns every declarative subscriber in <paramref name="publisher"/>'s brain that should be
    /// nudged for <paramref name="aliases"/>. Never returns <paramref name="publisher"/> itself.
    /// </summary>
    public IEnumerable<NeuronId> Hearing(
        NeuronId publisher,
        IReadOnlyCollection<string> aliases,
        Func<string, Type?> neuronClassOf,
        Func<string, Type?> signalTypeOf)
    {
        var snapshot = Volatile.Read(ref _all);
        if (snapshot.Length == 0 || aliases.Count == 0)
        {
            return [];
        }

        var publisherClass = neuronClassOf(publisher.Type);
        HashSet<NeuronId>? matched = null;
        foreach (var rule in snapshot)
        {
            var subscriber = new NeuronId(publisher.Brain, rule.SubscriberType, DefaultKey);
            if (subscriber == publisher)
            {
                continue;
            }

            foreach (var alias in aliases)
            {
                if (Matches(rule, publisherClass, alias, signalTypeOf(alias)))
                {
                    matched ??= [];
                    matched.Add(subscriber);
                    break;
                }
            }
        }

        return matched ?? (IEnumerable<NeuronId>)[];
    }

    /// <summary>
    /// Returns declarative subscribers in <paramref name="brain"/> that have no persisted subscription yet
    /// but have matching signals in <paramref name="journal"/> (e.g., after a crash before fanout).
    /// </summary>
    public IReadOnlyList<NeuronId> Due(
        BrainId brain,
        IJournalView journal,
        Func<string, Type?> neuronClassOf,
        Func<string, Type?> signalTypeOf)
    {
        var snapshot = Volatile.Read(ref _all);
        if (snapshot.Length == 0)
        {
            return [];
        }

        HashSet<NeuronId>? due = null;
        foreach (var mark in journal.Marks(null))
        {
            if (mark.Publisher.Brain != brain)
            {
                continue;
            }

            foreach (var subscriber in Hearing(mark.Publisher, [mark.Alias], neuronClassOf, signalTypeOf))
            {
                if (journal.Subscriptions.Of(subscriber) is null)
                {
                    due ??= [];
                    due.Add(subscriber);
                }
            }
        }

        return due is null ? [] : [.. due];
    }

    private static bool Matches(DeclarativeReactRule rule, Type? publisherClass, string alias, Type? signalType)
    {
        var aliasMatch = string.Equals(rule.SignalAlias, alias, StringComparison.Ordinal)
                         || (signalType is not null && rule.SignalType.IsAssignableFrom(signalType));
        if (!aliasMatch)
        {
            return false;
        }

        if (rule.SourceType is null || rule.SourceType == typeof(object) || rule.SourceType == typeof(Neuron))
        {
            return true;
        }

        return publisherClass is not null && rule.SourceType.IsAssignableFrom(publisherClass);
    }

    private static DeclarativeReactRule[] ExtractRules(string neuronType, Type neuronClass)
    {
        var rules = new List<DeclarativeReactRule>();
        var coveredSignals = new HashSet<Type>();
        var seenPairs = new HashSet<(Type?, Type)>();

        foreach (var attr in neuronClass.GetCustomAttributes<ReactsAttribute>(inherit: true))
        {
            if (attr.Signal is null || !typeof(Signal).IsAssignableFrom(attr.Signal))
            {
                continue;
            }

            if (seenPairs.Add((attr.Source, attr.Signal)))
            {
                coveredSignals.Add(attr.Signal);
                rules.Add(new DeclarativeReactRule(
                    neuronType,
                    attr.Source,
                    attr.Signal,
                    SignalAliases.Of(attr.Signal)));
            }
        }

        foreach (var signalType in ComponentManifests.ReactsOf(neuronClass))
        {
            if (signalType is null || !typeof(Signal).IsAssignableFrom(signalType))
            {
                continue;
            }

            if (!coveredSignals.Contains(signalType)
                && ReactionTable.Find(neuronClass, signalType) is not null
                && seenPairs.Add((null, signalType)))
            {
                rules.Add(new DeclarativeReactRule(
                    neuronType,
                    null,
                    signalType,
                    SignalAliases.Of(signalType)));
            }
        }

        return rules.Count == 0 ? [] : [.. rules];
    }
}
