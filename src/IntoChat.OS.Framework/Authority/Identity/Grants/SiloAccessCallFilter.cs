using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Authority.Identity.Grants;

public sealed class SiloAccessCallFilter(IServiceProvider services) : IIncomingGrainCallFilter
{
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
                await AuthorizeNeuronCallAsync(context, call).ConfigureAwait(false);
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
            targetBrain = EdgeGrantsCache.ExtractBrain(rawKey);
        }

        // Resolve ambient or stamped caller
        var callerAmbient = services.GetService<ICallerAmbient>()?.Current;
        Caller? caller = callerAmbient;
        if (caller is null && RuntimeCallerContext.Current is { } capCaller)
        {
            caller = capCaller.ToCaller();
        }

        // Handle unstamped caller provenance
        if (caller is null)
        {
            if (!(services.GetService<AccessOptions>()?.UnstampedEdgeIsKernel ?? true))
            {
                throw new UnauthorizedAccessException($"Unstamped call to '{targetNeuron}' carried no caller provenance.");
            }

            return;
        }

        var operation = string.IsNullOrEmpty(call.Contract) ? call.Method : $"{call.Contract}.{call.Method}";

        var request = new CallRequest
        {
            Caller = caller,
            TargetNeuron = targetNeuron,
            Operation = operation,
            TargetScope = string.IsNullOrEmpty(targetBrain) ? null : targetBrain,
            EnforceTarget = true,
        };

        var stages = services.GetServices<ICallFilterStage>().OrderBy(s => s.Order).ToArray();
        if (stages.Length == 0)
        {
            if (services.GetService<IntoChat.OS.Authority.Hosting.AccessPolicyValidation>() is not null)
            {
                throw new InvalidOperationException("Fail-closed access policy: no ICallFilterStage is registered in the silo.");
            }
            return;
        }

        CallDecision? decision = null;
        foreach (var stage in stages)
        {
            decision = await stage.EvaluateAsync(request).ConfigureAwait(false);
            if (decision is not null)
            {
                break;
            }
        }
        decision ??= CallDecision.Allow();

        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException(decision.Explanation ?? $"Call to '{targetNeuron}.{call.Method}' denied: {decision.Denial}");
        }
    }

    private void AuthorizeGrainCall(IIncomingGrainCallContext context)
    {
        var callerAmbient = services.GetService<ICallerAmbient>()?.Current;
        var capCaller = RuntimeCallerContext.Current;
        var caller = callerAmbient ?? capCaller?.ToCaller();

        if (caller is not null)
        {
            var capabilityCaller = caller.ToCapabilityCaller();
            if (capabilityCaller is not null && (capabilityCaller.Kind == CallerKind.App || AppPrincipal.IsApp(capabilityCaller.PrincipalId)))
            {
                var ifaceName = context.InterfaceMethod.DeclaringType?.Name ?? "";
                var methodName = context.InterfaceMethod.Name;
                var request = new CallRequest
                {
                    Caller = caller,
                    TargetNeuron = ifaceName,
                    Operation = $"{ifaceName}.{methodName}",
                    TargetScope = caller.Brain.Value,
                    EnforceTarget = true,
                };

                if (GrantRules.EvaluateZeroTokenGuard(request) is { } denial && !denial.Allowed)
                {
                    throw new UnauthorizedAccessException(denial.Explanation ?? $"Zero-token guard denied access to {ifaceName}.{methodName}");
                }
            }
        }
    }
}
