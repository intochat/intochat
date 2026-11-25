using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using Orleans;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Identity.Grants;

// The silo's admission rule for grain calls. An OS silo has no external callers: the product ships no out-of-process cluster
// client (the command-line host calls the edge), so a grain call this silo process did not send (SiloOrigin) is refused, whatever
// caller it states and whichever port it came in on. SingleSiloGuard keeps the silo alone in its cluster, which is what makes
// "sent by this process" the same as "sent from inside". Of the calls the process sent, a neuron contract call (INeuronHost with
// a RuntimeInvocation) is authorized by the stages, and every other grain method needs a stamped caller, except the entries below.
public sealed class SiloAccessCallFilter(
    IEnumerable<ICallFilterStage> stages,
    ICallerAmbient callerAmbient) : IIncomingGrainCallFilter
{
    // Entry points that take a call with no caller. Each is sent by this silo process itself.
    private static readonly (Type Contract, string Method)[] Callerless =
    [
        // The Orleans reminder service delivers a tick and knows no caller; the host grain runs it as the scheduler of its brain.
        (typeof(IRemindable), nameof(IRemindable.ReceiveReminder)),
        // Which host grain type serves a neuron type: a proxy asks before it stamps the hop, and the answer is build metadata.
        (typeof(INeuronHostCatalog), nameof(INeuronHostCatalog.HostType)),
        (typeof(ICompiledTypeTable), nameof(ICompiledTypeTable.HostType)),
    ];

    private readonly ICallFilterStage[] _stages = [.. (stages ?? throw new ArgumentNullException(nameof(stages))).OrderBy(static s => s.Order)];
    private readonly ICallerAmbient _callerAmbient = callerAmbient ?? throw new ArgumentNullException(nameof(callerAmbient));

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 1. Ignore Orleans internal system grains
        if (Orleans.Runtime.SystemTargetGrainId.TryParse(context.TargetId, out _))
        {
            await context.Invoke();
            return;
        }

        var targetTypeStr = context.TargetId.Type.ToString();
        if (targetTypeStr is not null &&
            (targetTypeStr.StartsWith("sys.", StringComparison.OrdinalIgnoreCase) ||
             targetTypeStr.StartsWith("orleans.", StringComparison.OrdinalIgnoreCase)))
        {
            await context.Invoke();
            return;
        }

        // 2. Nothing from outside this process, on any port: no grain, no Orleans grain, no neuron contract.
        if (!SiloOrigin.IsCurrent)
        {
            throw new UnauthorizedAccessException($"'{Name(context)}' was not sent by this silo process: an OS silo has no external callers.");
        }

        // 3. Orleans' own plumbing: its grain extensions (stream enumeration, call cancellation) ride on an admitted call, and the
        // grains it implements itself (management, the dashboard when a host turns it on) hold no brain's state.
        if (IsOrleansPlumbing(context))
        {
            await context.Invoke();
            return;
        }

        // 4. Neuron contract calls arrive on INeuronHost with a RuntimeInvocation; the stages authorize them and refuse a missing caller.
        // The interleaving check binds the same invocation but executes no neuron method, so it takes the default rule instead.
        if (context.Grain is INeuronHost
            && !Is(context, typeof(INeuronHost), nameof(INeuronHost.IsInterleaved))
            && context.Request.GetArgumentCount() > 0 && context.Request.GetArgument(0) is RuntimeInvocation call)
        {
            await AuthorizeNeuronCallAsync(context, call);
            await context.Invoke();
            return;
        }

        // 5. Every other grain method: refused without a caller.
        RequireAdmitted(context);
        if (context.Grain is not INeuronHost)
        {
            // Non-INeuronHost grain calls: Zero-Token enforcement for app callers attempting direct access
            AuthorizeGrainCall(context);
        }

        await context.Invoke();
    }

    private void RequireAdmitted(IIncomingGrainCallContext context)
    {
        if (_callerAmbient.Current is null && !Listed(Callerless, context))
        {
            throw new UnauthorizedAccessException($"Unstamped call to '{Name(context)}' carried no caller provenance.");
        }
    }

    private static bool IsOrleansPlumbing(IIncomingGrainCallContext context)
        => (context.InterfaceMethod.DeclaringType is { } contract && typeof(IGrainExtension).IsAssignableFrom(contract) && IsOrleans(contract))
            || (context.Grain is { } grain && IsOrleans(grain.GetType()));

    private static bool IsOrleans(Type type)
        => type.Assembly.GetName().Name is { } assembly
            && (assembly.StartsWith("Orleans.", StringComparison.Ordinal) || assembly.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal));

    private static bool Is(IIncomingGrainCallContext context, Type contract, string method)
        => context.InterfaceMethod.DeclaringType == contract && context.InterfaceMethod.Name == method;

    private static bool Listed((Type Contract, string Method)[] list, IIncomingGrainCallContext context)
    {
        foreach (var (contract, method) in list)
        {
            if (Is(context, contract, method)) { return true; }
        }
        return false;
    }

    private static string Name(IIncomingGrainCallContext context)
        => $"{context.InterfaceMethod.DeclaringType?.Name}.{context.InterfaceMethod.Name}";

    private async Task AuthorizeNeuronCallAsync(IIncomingGrainCallContext context, RuntimeInvocation call)
    {
        var rawKey = context.TargetId.Key.ToString() ?? call.Key ?? "";
        string targetNeuron;
        string targetBrain;

        if (NeuronId.TryParse(rawKey, out var neuronId))
        {
            targetNeuron = $"{neuronId.Type}/{neuronId.Brain.Value}/{neuronId.Key}";
            targetBrain = neuronId.Brain.Value;
        }
        else if (!string.IsNullOrWhiteSpace(call.Key) && NeuronId.TryParse(call.Key, out var callNeuronId))
        {
            targetNeuron = $"{callNeuronId.Type}/{callNeuronId.Brain.Value}/{callNeuronId.Key}";
            targetBrain = callNeuronId.Brain.Value;
        }
        else
        {
            targetNeuron = rawKey;
            targetBrain = EdgeGrantsCache.TryExtractBrain(rawKey, out var parsedBrain) ? parsedBrain.Value : string.Empty;
        }

        // No caller is refused: the kernel is only ever an explicitly entered caller.
        var caller = _callerAmbient.Current
            ?? throw new UnauthorizedAccessException($"Unstamped call to '{targetNeuron}' carried no caller provenance.");

        if (_stages.Length == 0)
        {
            throw new UnauthorizedAccessException("Fail-closed access policy: no ICallFilterStage is registered in the silo.");
        }

        var operation = string.IsNullOrEmpty(call.Contract) ? call.Method : $"{call.Contract}.{call.Method}";

        var request = new CallRequest
        {
            TargetNeuron = targetNeuron,
            Operation = operation,
            TargetScope = string.IsNullOrEmpty(targetBrain) ? null : targetBrain,
            EnforceTarget = true,
        };

        CallDecision? decision = null;
        foreach (var stage in _stages)
        {
            decision = await stage.EvaluateAsync(caller, request);
            if (decision is not null)
            {
                break;
            }
        }

        decision ??= CallDecision.Deny(
            CallDenial.MissingGrant,
            $"No authorization stage returned a decision for '{targetNeuron}.{operation}'.");

        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException(decision.Explanation ?? $"Call to '{targetNeuron}.{call.Method}' denied: {decision.Denial}");
        }
    }

    private void AuthorizeGrainCall(IIncomingGrainCallContext context)
    {
        var caller = _callerAmbient.Current;

        if (caller is not null && GrantRules.IsAppCaller(caller))
        {
            var ifaceName = context.InterfaceMethod.DeclaringType?.Name ?? "";
            var methodName = context.InterfaceMethod.Name;
            var request = new CallRequest
            {
                TargetNeuron = ifaceName,
                Operation = $"{ifaceName}.{methodName}",
                TargetScope = caller.Brain.Value,
                EnforceTarget = true,
            };

            if (GrantRules.EvaluateZeroTokenGuard(caller, request) is { } denial && !denial.Allowed)
            {
                throw new UnauthorizedAccessException(denial.Explanation ?? $"Zero-token guard denied access to {ifaceName}.{methodName}");
            }
        }
    }
}
