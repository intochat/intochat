using DigitalBrain.Brains;
using DigitalBrain.Contracts;
using System.Reflection;
using DigitalBrain.Signals.Watch;
using DigitalBrain.Spi;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The IntoChat.OS assembly is what the kernel's compiled layer ships into the sandbox: the four words and the
// shapes they carry, nothing else. Anything that fits none of the clauses below belongs in a ring
// behind it (Framework, Platform, Client); widening the allowlist or the namespace list is a design decision.
public sealed class VocabularyRingFacts
{
    private static readonly Assembly Vocabulary = typeof(IBrain).Assembly;

    // The neuron-abstraction ring below IntoChat.OS: the DigitalBrain core packages (CORE-02). Script-visible
    // contracts may depend on IntoChat.OS and these only.
    private static readonly string[] CoreAbstractions = ["DigitalBrain", "DigitalBrain.UI"];

    // IBrainObserver is the ratified script-visible kernel slice (kernel design Q8). ILogic<> is what a rule
    // implements (spec 2026-10-10 section 6): like Synapse, a shape scripts build on, not one a contract carries.
    private static readonly Type[] Roots = [typeof(IBrain), typeof(ILogic<>), typeof(ISignalWatch)];

    // The Contracts ring: IntoChat.OS plus every production assembly whose IntoChat.OS references stay
    // inside the ring (module *.Contracts projects). Their neuron contracts, signals and wire exceptions
    // are what scripts compile against, so they root reachability for the vocabulary's shapes.
    private static readonly Assembly[] Ring = ContractsRing();

    // A module that hosts its own contracts (IntoChatOSContractsHost) sits outside the ring by its implementation
    // references; its contract types still root the vocabulary's reachability.
    private static readonly HashSet<Type> HostContracts = [.. ProductionArchitecture.Assemblies.Where(ContractRing.IsHost).SelectMany(ContractRing.ContractTypes)];

    private static readonly Dictionary<Type, string> Allowlist = new()
    {
        [typeof(NeuronAliases)] = "A type's effective alias across [NeuronAlias] (DigitalBrain.Contracts) and the Orleans [Alias]; the Orleans half keeps it out of the core package (03-03).",
        [typeof(NeuronKeys)] = "Canonical '{brain}/{key}' neuron key formatting across OS modules (phase 3 Task 6).",
        [typeof(Trace)] = "The compute-cost record (alias brain.trace). Origin is a DigitalBrain.NeuronId. Signal traces are Activity.Current.",
    };

    // IntoChat.OS.Runtime is the runtime SPI (clean-core 2.3): what a runtime implements and a neuron is handed.
    // It is not vocabulary a contract carries, so it is judged by clause (g) instead of reachability.
    private const string RuntimeNamespace = "IntoChat.OS.Runtime";

    private static readonly string[] Namespaces = ["IntoChat.OS", RuntimeNamespace];

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
                || CoreAbstractions.Contains(name)
                || name is "netstandard" or "mscorlib" or "Microsoft.Orleans.Core.Abstractions" or "Microsoft.Orleans.Serialization.Abstractions"
                    or "Orleans.Core.Abstractions" or "Orleans.Serialization.Abstractions" or "Orleans.Serialization"
                || (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && name.EndsWith(".Abstractions", StringComparison.Ordinal))))
            .Order().ToArray();
        Assert.True(offenders.Length == 0, "IntoChat.OS references non-abstraction assemblies:\n" + string.Join("\n", offenders));
    }

    // Clause (g) admits exactly the clean-core 2.3 SPI; a new runtime type is a design change, made here.
    [Fact]
    public void TheRuntimeSpiIsExactlyTheDesignedSet()
    {
        // The Orleans-bound half stays legacy in IntoChat.OS.Runtime; the pure half comes from the DigitalBrain package (03-03 name map).
        Assert.Equal(
            [
                "DigitalBrain.Contracts.ConnectionContractAttribute",
                "IntoChat.OS.Runtime.INeuronStateStore", "IntoChat.OS.Runtime.INeuronState`1",
                "IntoChat.OS.Runtime.IRuntimeBuilder",
                "IntoChat.OS.Runtime.NeuronProxies", "DigitalBrain.Contracts.PlainNeuronContracts"],
            PublicTypes().Where(IsRuntimeSpi).Select(t => t.FullName!).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["DigitalBrain.BrainId", "DigitalBrain.DeactivationCause", "DigitalBrain.State.IStateCodec", "DigitalBrain.State.StoredRecord", "DigitalBrain.State.WriteResult"],
            new[] { typeof(DigitalBrain.BrainId), typeof(DigitalBrain.DeactivationCause), typeof(DigitalBrain.State.IStateCodec),
                typeof(DigitalBrain.State.StoredRecord), typeof(DigitalBrain.State.WriteResult) }.Select(t => t.FullName!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(PublicTypes(), t => t.Name is "BrainId" or "DeactivationCause");
    }

    [Fact]
    public void TheRingHoldsNoHandles()
    {
        // A Task is itself disposable; what matters is what a member hands back once awaited.
        static Type Awaited(Type type) => type == typeof(Task) || type == typeof(ValueTask) ? typeof(void)
            : type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)) ? type.GetGenericArguments()[0]
            : type;
        static bool IsHandle(Type type) => typeof(IAsyncDisposable).IsAssignableFrom(type) || typeof(IDisposable).IsAssignableFrom(type);
        // The runtime SPI hands out scopes (a timer, a suppressed caller, an entered trace); contracts never do.
        var offenders = PublicTypes().Where(t => !IsRuntimeSpi(t))
            .Where(t => IsHandle(t)
                || t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(m => IsHandle(Awaited(m.ReturnType))))
            .Select(t => t.FullName!).Order().ToArray();
        Assert.True(offenders.Length == 0, "Ring types that are or return handles:\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<Type> PublicTypes() => Vocabulary.GetExportedTypes()
        .Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true);

    private static bool IsContract(Type t) => Roots.Contains(t) || (t.IsInterface && DigitalBrain.Contracts.PlainNeuronContracts.Is(t));

    private static bool IsRuntimeSpi(Type t) => t.Namespace == RuntimeNamespace;

    private static bool IsSignal(Type t) => typeof(DigitalBrain.Signal).IsAssignableFrom(t);

    private static bool IsSerializable(Type t) => t.GetCustomAttributes(inherit: false).Any(a => a.GetType().Name == "GenerateSerializerAttribute");

    // An exception that crosses a grain call to a script: declared in the ring and [GenerateSerializer].
    private static bool IsWireException(Type t) => typeof(Exception).IsAssignableFrom(t) && IsSerializable(t);

    private static bool Fits(Type t, HashSet<Type> reachable) =>
        IsContract(t) || IsSignal(t) || IsWireException(t) || IsRuntimeSpi(t) || typeof(Attribute).IsAssignableFrom(t) || Allowlist.ContainsKey(t)
        || (reachable.Contains(t) && (t.IsEnum || IsSerializable(t)));

    private static string Describe(Type t, HashSet<Type> reachable)
    {
        var kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : "class";
        var clause = !reachable.Contains(t) ? "(c) not reachable from a Contracts-ring neuron contract, signal or wire exception"
            : "(c) reachable but not [GenerateSerializer]";
        if (typeof(Exception).IsAssignableFrom(t)) { clause = "(e) exception not [GenerateSerializer]"; }
        return $"{t.FullName} [{kind}] fails {clause}; not (a) contract, (b) signal, (d) attribute, (f) allowlisted or (g) runtime SPI";
    }

    private static HashSet<Type> Reachable()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        void Visit(Type type)
        {
            // Walk shapes through the whole ring (a Contracts record can carry a vocabulary enum),
            // but only vocabulary types are judged by the rule.
            foreach (var part in Unwrap(type).Where(p => (Ring.Contains(p.Assembly) || HostContracts.Contains(p)) && seen.Add(p))) { queue.Enqueue(part); }
        }
        var ringTypes = Ring.SelectMany(a => a.GetExportedTypes()).Concat(HostContracts)
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
        var ring = new HashSet<string>(CoreAbstractions.Append(Vocabulary.GetName().Name!), StringComparer.Ordinal);
        bool grew;
        do
        {
            grew = false;
            foreach (var (name, assembly) in candidates.Where(c => !ring.Contains(c.Key)))
            {
                var ownReferences = assembly.GetReferencedAssemblies().Select(r => r.Name!)
                    .Where(r => r.StartsWith("IntoChat.OS", StringComparison.Ordinal) || r.StartsWith("DigitalBrain", StringComparison.Ordinal)).ToArray();
                if (ownReferences.Length > 0 && ownReferences.All(ring.Contains)) { grew |= ring.Add(name); }
            }
        }
        while (grew);
        return [.. ring.Where(candidates.ContainsKey).Select(name => candidates[name])];
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
