using System.Reflection;

namespace DigitalBrain.Architecture.Tests;

// The DigitalBrain assembly is what ScriptContracts ships into the sandbox: the four words and the
// shapes they carry, nothing else. Anything that fits none of the clauses below belongs in a ring
// behind it (Framework, Platform, Client); widening the allowlist or the namespace list is a design decision.
public sealed class VocabularyRingFacts
{
    private static readonly Assembly Vocabulary = typeof(Signal).Assembly;

    private static readonly Type[] Roots = [typeof(INeuron), typeof(IBrain), typeof(IDigitalBrain), typeof(INeuronObserver), typeof(ISignalSubscription<>)];

    // The Contracts ring: DigitalBrain plus every production assembly whose DigitalBrain references stay
    // inside the ring (module *.Contracts projects). Their neuron contracts, signals and wire exceptions
    // are what scripts compile against, so they root reachability for the vocabulary's shapes.
    private static readonly Assembly[] Ring = ContractsRing();

    private static readonly Dictionary<Type, string> Allowlist = new();

    private static readonly string[] Namespaces =
        ["DigitalBrain", "DigitalBrain.Integrations", "DigitalBrain.Types"];

    [Fact]
    public void EveryPublicVocabularyTypeIsANeuronContractASignalOrAShapeTheyCarry()
    {
        var reachable = Reachable();
        var offenders = PublicTypes().Where(t => !Fits(t, reachable)).Select(t => Describe(t, reachable)).Order().ToArray();
        Assert.True(offenders.Length == 0, "Types outside the vocabulary rule:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void VocabularyTypesLiveOnlyInTheVocabularyNamespaces()
    {
        var offenders = PublicTypes().Where(t => !Namespaces.Contains(t.Namespace)).Select(t => t.FullName + " (" + t.Namespace + ")").Order().ToArray();
        Assert.True(offenders.Length == 0, "Types in a namespace outside the vocabulary:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void VocabularyReferencesOnlyAbstractions()
    {
        var offenders = Vocabulary.GetReferencedAssemblies().Select(a => a.Name!).Where(name => !(
                name.StartsWith("System", StringComparison.Ordinal)
                || name is "netstandard" or "mscorlib" or "Microsoft.Orleans.Core.Abstractions" or "Microsoft.Orleans.Serialization.Abstractions"
                    or "Orleans.Core.Abstractions" or "Orleans.Serialization.Abstractions" or "Orleans.Serialization"
                || (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && name.EndsWith(".Abstractions", StringComparison.Ordinal))))
            .Order().ToArray();
        Assert.True(offenders.Length == 0, "DigitalBrain references non-abstraction assemblies:\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<Type> PublicTypes() => Vocabulary.GetExportedTypes()
        .Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true);

    private static bool IsContract(Type t) => Roots.Contains(t) || (t.IsInterface && typeof(INeuron).IsAssignableFrom(t));

    private static bool IsSignal(Type t) => typeof(Signal).IsAssignableFrom(t);

    private static bool IsSerializable(Type t) => t.GetCustomAttributes(inherit: false).Any(a => a.GetType().Name == "GenerateSerializerAttribute");

    // An exception that crosses a grain call to a script: declared in the ring and [GenerateSerializer].
    private static bool IsWireException(Type t) => typeof(Exception).IsAssignableFrom(t) && IsSerializable(t);

    private static bool Fits(Type t, HashSet<Type> reachable) =>
        IsContract(t) || IsSignal(t) || IsWireException(t) || typeof(Attribute).IsAssignableFrom(t) || Allowlist.ContainsKey(t)
        || (reachable.Contains(t) && (t.IsEnum || IsSerializable(t)));

    private static string Describe(Type t, HashSet<Type> reachable)
    {
        var kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : "class";
        var clause = !reachable.Contains(t) ? "(c) not reachable from a Contracts-ring neuron contract, signal or wire exception"
            : "(c) reachable but not [GenerateSerializer]";
        if (typeof(Exception).IsAssignableFrom(t)) { clause = "(e) exception not [GenerateSerializer]"; }
        return $"{t.FullName} [{kind}] fails {clause}; not (a) contract, (b) signal, (d) attribute or (f) allowlisted";
    }

    private static HashSet<Type> Reachable()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        void Visit(Type type)
        {
            // Walk shapes through the whole ring (a Contracts record can carry a vocabulary enum),
            // but only vocabulary types are judged by the rule.
            foreach (var part in Unwrap(type).Where(p => Ring.Contains(p.Assembly) && seen.Add(p))) { queue.Enqueue(part); }
        }
        var ringTypes = Ring.SelectMany(a => a.GetExportedTypes())
            .Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true).ToArray();
        foreach (var contract in ringTypes.Where(IsContract))
        {
            foreach (var method in contract.GetMethods())
            {
                Visit(method.ReturnType);
                foreach (var parameter in method.GetParameters()) { Visit(parameter.ParameterType); }
            }
        }
        foreach (var root in ringTypes.Where(t => IsSignal(t) || IsWireException(t))) { Members(root).ToList().ForEach(Visit); }
        while (queue.TryDequeue(out var shape))
        {
            if (!IsContract(shape) && !IsSignal(shape) && !IsWireException(shape)) { Members(shape).ToList().ForEach(Visit); }
        }
        return [.. seen.Where(t => t.Assembly == Vocabulary)];
    }

    private static IEnumerable<Type> Members(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.PropertyType)
        .Concat(t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));

    private static Assembly[] ContractsRing()
    {
        var candidates = ProductionArchitecture.Assemblies.Append(Vocabulary).Distinct()
            .ToDictionary(a => a.GetName().Name!, StringComparer.Ordinal);
        var ring = new HashSet<string>(StringComparer.Ordinal) { Vocabulary.GetName().Name! };
        bool grew;
        do
        {
            grew = false;
            foreach (var (name, assembly) in candidates.Where(c => !ring.Contains(c.Key)))
            {
                var ownReferences = assembly.GetReferencedAssemblies().Select(r => r.Name!)
                    .Where(r => r.StartsWith("DigitalBrain", StringComparison.Ordinal)).ToArray();
                if (ownReferences.Length > 0 && ownReferences.All(ring.Contains)) { grew |= ring.Add(name); }
            }
        }
        while (grew);
        return [.. ring.Select(name => candidates[name])];
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsByRef || type.IsArray) { return Unwrap(type.GetElementType()!); }
        if (type.IsGenericParameter) { return type.GetGenericParameterConstraints().SelectMany(Unwrap); }
        if (type.IsGenericType)
        {
            return type.GetGenericArguments().SelectMany(Unwrap).Prepend(type.GetGenericTypeDefinition());
        }
        return [type];
    }
}
