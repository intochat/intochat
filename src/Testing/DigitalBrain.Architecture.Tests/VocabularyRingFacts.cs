using System.Reflection;

namespace DigitalBrain.Architecture.Tests;

// The DigitalBrain assembly is what the kernel's compiled layer ships into the sandbox: the four words and the
// shapes they carry, nothing else. Anything that fits none of the clauses below belongs in a ring
// behind it (Framework, Platform, Client); widening the allowlist or the namespace list is a design decision.
public sealed class VocabularyRingFacts
{
    private static readonly Assembly Vocabulary = typeof(Signal).Assembly;

    // IKernelView is the ratified script-visible kernel slice (kernel design Q8). ILogic<> is what a rule
    // implements (spec 2026-10-10 section 6): like Synapse, a shape scripts build on, not one a contract carries.
    private static readonly Type[] Roots = [typeof(INeuron), typeof(IBrain), typeof(Synapse), typeof(ILogic<>), typeof(IDigitalBrain), typeof(INeuronObserver), typeof(IKernelView)];

    // The Contracts ring: DigitalBrain plus every production assembly whose DigitalBrain references stay
    // inside the ring (module *.Contracts projects). Their neuron contracts, signals and wire exceptions
    // are what scripts compile against, so they root reachability for the vocabulary's shapes.
    private static readonly Assembly[] Ring = ContractsRing();

    private static readonly Dictionary<Type, string> Allowlist = new()
    {
        [typeof(Sources)] = "A set of neurons for IDigitalBrain.On/React: names neurons through a delegate, never crosses the wire (spec 2026-10-10 synapse subscription contract).",
        [typeof(ISelectiveWatch)] = "Optional watch. A behavior names one operation and the runtime does not start it for the others. Callers that only have IDigitalBrain keep that filter.",
    };

    private static readonly string[] Namespaces = ["DigitalBrain"];

    [Fact]
    public void EveryPublicVocabularyTypeIsANeuronContractASignalOrAShapeTheyCarry()
    {
        var reachable = Reachable();
        var offenders = PublicTypes().Where(t => !Fits(t, reachable)).Select(t => Describe(t, reachable)).Order().ToArray();
        Assert.True(offenders.Length == 0, "Types outside the vocabulary rule:\n" + string.Join("\n", offenders));
    }

    // Every assembly that sees the vocabulary's internals is a consumer the ring cannot be audited without;
    // adding one is a design decision, made here.
    private static readonly string[] InternalsConsumers =
        ["DigitalBrain.Client", "DigitalBrain.Client.Orleans.Tests.Unit", "DigitalBrain.Client.Tests.Unit", "DigitalBrain.Modules.Apps.Contracts"];

    [Fact]
    public void OnlyTheAllowedAssembliesSeeTheVocabularysInternals()
    {
        var granted = Vocabulary.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(InternalsConsumers, granted);
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

    [Fact]
    public void TheRingHoldsNoHandles()
    {
        // A Task is itself disposable; what matters is what a member hands back once awaited.
        static Type Awaited(Type type) => type == typeof(Task) || type == typeof(ValueTask) ? typeof(void)
            : type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)) ? type.GetGenericArguments()[0]
            : type;
        static bool IsHandle(Type type) => typeof(IAsyncDisposable).IsAssignableFrom(type) || typeof(IDisposable).IsAssignableFrom(type);
        var offenders = PublicTypes()
            .Where(t => IsHandle(t)
                || t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(m => IsHandle(Awaited(m.ReturnType))))
            .Select(t => t.FullName!).Order().ToArray();
        Assert.True(offenders.Length == 0, "Ring types that are or return handles:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheBrainSeenByAProgramHasExactlyGetOnSelfAndKernel()
    {
        var methods = typeof(IDigitalBrain).GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name).Order().ToArray();
        Assert.Equal(["Get", "On", "On", "On", "React", "React"], methods);
        var watches = typeof(IDigitalBrain).GetMethods().Where(m => m.Name == "On").OrderBy(m => m.GetParameters().Length).ThenBy(m => m.GetParameters()[0].ParameterType.Name).ToArray();
        Assert.Equal([typeof(INeuron), typeof(CancellationToken)], watches[0].GetParameters().Select(p => p.ParameterType));
        Assert.Equal([typeof(Sources), typeof(CancellationToken)], watches[1].GetParameters().Select(p => p.ParameterType));
        Assert.Equal([typeof(INeuron), typeof(Func<Task>), typeof(CancellationToken)], watches[2].GetParameters().Select(p => p.ParameterType));
        var reacts = typeof(IDigitalBrain).GetMethods().Where(m => m.Name == "React").OrderBy(m => m.GetParameters()[0].ParameterType.Name).ToArray();
        Assert.Equal([typeof(INeuron), typeof(Func<CancellationToken, Task>), typeof(CancellationToken)], reacts[0].GetParameters().Select(p => p.ParameterType));
        Assert.Equal([typeof(Sources), typeof(Func<CancellationToken, Task>), typeof(CancellationToken)], reacts[1].GetParameters().Select(p => p.ParameterType));
        Assert.Equal(["Kernel", "Self"], typeof(IDigitalBrain).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(typeof(IKernelView), typeof(IDigitalBrain).GetProperty("Kernel")!.PropertyType);
        Assert.Equal(typeof(IBrain), typeof(IDigitalBrain).GetProperty("Self")!.PropertyType);
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IDigitalBrain)));
        var get = typeof(IDigitalBrain).GetMethod("Get")!;
        Assert.Contains(typeof(INeuron), get.GetGenericArguments()[0].GetGenericParameterConstraints());
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
