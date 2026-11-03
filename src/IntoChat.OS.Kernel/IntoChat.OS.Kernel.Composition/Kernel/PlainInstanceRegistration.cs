using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using IntoChatOSNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-06d, until 06-08 gives the runtime its own neuron registry): the kernel's instance directory for plain neurons.
// The legacy storage decorator (InstanceRegistration) registers a grain on its first write; a plain neuron's state goes through the
// runtime's journal, which offers no write hook, so this filter watches the calls into the runtime's host grains instead. After a call
// that is not read-only it asks the cluster store whether the neuron now has state: a first record registers "{brain}/{key}" under
// the neuron type in the brain's directory, a cleared record retires it. Only a change is written, once per activation, so
// Kernel.Instances<T> lists plain neurons as it lists grains, kernel-stamped and never by module code.
internal sealed class PlainInstanceRegistration(IServiceProvider services, IGrainFactory grains) : IIncomingGrainCallFilter
{
    private static readonly ConcurrentDictionary<string, string?> StateNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Contract, string Method, int Arity), bool> ReadOnly = new();
    private readonly ConditionalWeakTable<object, Known> _known = new();

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is not INeuronHost || context.Request.GetArgumentCount() == 0 || context.Request.GetArgument(0) is not RuntimeInvocation call)
        { await context.Invoke(); return; }
        // Host policy discovery executes no neuron method and must not mutate the instance projection.
        if (context.InterfaceMethod.DeclaringType == typeof(INeuronHost) && context.InterfaceMethod.Name == nameof(INeuronHost.IsInterleaved))
        { await context.Invoke(); return; }
        // A call that fails after its first write still created the neuron (06-08: an app whose deploy timed out after its record
        // landed), so the record is checked whatever the call's outcome; the call's own failure is what the caller sees.
        try { await context.Invoke(); }
        catch
        {
            await RegisterAsync(context, call);
            throw;
        }
        await RegisterAsync(context, call);
    }

    private async Task RegisterAsync(IIncomingGrainCallContext context, RuntimeInvocation call)
    {
        if (IsReadOnly(call) || !DigitalBrain.NeuronId.TryParse(context.TargetId.Key.ToString()!, out var id)) { return; }
        if (StateNameOf(id.Type) is not { } stateName) { return; }
        var fullKey = $"{id.Brain.Value}/{id.Key}";
        if (BrainKey.ScopeOf(fullKey) is not { } brain) { return; }
        if (services.GetKeyedService<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage) is not { } store) { return; }
        var state = new GrainState<JsonElement>();
        await store.ReadStateAsync(stateName, context.TargetId, state);
        var known = _known.GetOrCreateValue(context.Grain);
        if (known.Exists == state.RecordExists) { return; }
        var directory = grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, id.Type));
        if (state.RecordExists) { await directory.Register(fullKey, RuntimeCallerContext.TryGet(out var caller) ? caller.PrincipalId : null); }
        else if (known.Exists is not null) { await directory.Retire(fullKey); }
        known.Exists = state.RecordExists;
    }

    // The persisted state name of the class hosted for type, or null for a stateless or unknown class.
    internal static string? StateNameOf(string type) => StateNames.GetOrAdd(type, static name =>
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            Type[] candidates;
            try { candidates = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { candidates = [.. error.Types.OfType<Type>()]; }
            foreach (var candidate in candidates)
            {
                if (!candidate.IsClass || candidate.GetCustomAttribute<DigitalBrain.NeuronAttribute>(inherit: false) is not { } neuron
                    || !string.Equals(neuron.Type, name, StringComparison.Ordinal)) { continue; }
                return candidate.IsDefined(typeof(DigitalBrain.StatelessNeuronAttribute), inherit: false) ? null : neuron.State ?? "state";
            }
        }
        return null;
    });

    private static bool IsReadOnly(RuntimeInvocation call) => ReadOnly.GetOrAdd((call.Contract, call.Method, call.Arguments.Length), static key =>
    {
        if (KernelInvocationSink.ContractOf(key.Contract) is not { } contract) { return false; }
        var method = new[] { contract }.Concat(contract.GetInterfaces()).SelectMany(face => face.GetMethods())
            .FirstOrDefault(candidate => candidate.Name == key.Method
                && candidate.GetParameters().Count(parameter => parameter.ParameterType != typeof(CancellationToken)) == key.Arity);
        return method?.IsDefined(typeof(DigitalBrain.ReadOnlyCallAttribute), inherit: false) == true;
    });

    private sealed class Known
    {
        public bool? Exists { get; set; }
    }
}
