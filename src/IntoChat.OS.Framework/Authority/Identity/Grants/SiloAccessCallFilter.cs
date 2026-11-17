using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using Orleans;

namespace IntoChat.OS.Authority.Identity.Grants;

public sealed class SiloAccessCallFilter(
    IEnumerable<ICallFilterStage> stages,
    ICallerAmbient? callerAmbient = null,
    AccessOptions? accessOptions = null,
    EdgeAuthority? edgeAuthority = null) : IIncomingGrainCallFilter
{
    private readonly ICallFilterStage[] _stages = [.. (stages ?? throw new ArgumentNullException(nameof(stages))).OrderBy(static s => s.Order)];
    private readonly ICallerAmbient? _callerAmbient = callerAmbient;
    private readonly EdgeAuthority? _edgeAuthority = edgeAuthority;
    private readonly bool _unstampedEdgeIsKernel = accessOptions?.UnstampedEdgeIsKernel ?? false;

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

        // 2. Plain neuron calls arrive on INeuronHost
        if (context.Grain is INeuronHost)
        {
            // Interleaving check: host policy discovery executes no neuron method and must not be blocked or mutate projection.
            if (context.InterfaceMethod.DeclaringType == typeof(INeuronHost) &&
                context.InterfaceMethod.Name == nameof(INeuronHost.IsInterleaved))
            {
                await context.Invoke();
                return;
            }

            // Internal host management methods that do not execute user neuron contracts
            if (context.InterfaceMethod.DeclaringType == typeof(INeuronHost) &&
                context.InterfaceMethod.Name == nameof(INeuronHost.InspectState))
            {
                await context.Invoke();
                return;
            }

            // Neuron method invocations
            if (context.Request.GetArgumentCount() > 0 && context.Request.GetArgument(0) is RuntimeInvocation call)
            {
                await AuthorizeNeuronCallAsync(context, call);
            }
        }
        else
        {
            // Non-INeuronHost grain calls: Zero-Token enforcement for app callers attempting direct access
            AuthorizeGrainCall(context);
        }

        await context.Invoke();
    }

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

        var caller = ResolveCurrentCaller();

        // Unstamped in-process kernel scopes carry ProcessSecretToken via EdgeAuthority;
        // unstamped external/edge calls fail closed when UnstampedEdgeIsKernel is false.
        if (caller is null)
        {
            if (!(_edgeAuthority?.IsKernelActive ?? false) && !_unstampedEdgeIsKernel)
            {
                throw new UnauthorizedAccessException($"Unstamped call to '{targetNeuron}' carried no caller provenance.");
            }

            return;
        }

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
        var caller = ResolveCurrentCaller();

        if (caller is not null
            && (caller.Actor is Actor.App || (caller.Actor is Actor.Person person && AppPrincipal.IsApp(person.Principal))))
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

    private Caller? ResolveCurrentCaller()
        => _callerAmbient?.Current
            ?? (_edgeAuthority is not null && RuntimeCallerContext.Current is { } legacy ? _edgeAuthority.From(legacy) : null);
}
