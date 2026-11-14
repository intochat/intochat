// os-only: Bridges legacy CapabilityCaller to 0.20.0 DigitalBrain Caller
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;

namespace IntoChat.OS.Framework;

public static class CallerExtensions
{
    private static readonly ConstructorInfo CallerCtor = typeof(Caller)
        .GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Actor), typeof(BrainId), typeof(string), typeof(NeuronId), typeof(bool)],
            modifiers: null)!;

    public static Caller ToCaller(this CapabilityCaller legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var brain = new BrainId(legacy.BrainId);
        Actor actor = legacy.Kind switch
        {
            CallerKind.Platform => new Actor.Kernel(),
            CallerKind.Scheduler => new Actor.Scheduler(),
            CallerKind.Agent => new Actor.Agent(new Actor.Person(legacy.PrincipalId, legacy.AccountId)),
            CallerKind.App => new Actor.App(legacy.AppId ?? string.Empty, new Actor.Person(legacy.PrincipalId, legacy.AccountId)),
            _ => new Actor.Person(legacy.PrincipalId, legacy.AccountId)
        };
        return (Caller)CallerCtor.Invoke([actor, brain, legacy.ConversationId, null, false]);
    }

    public static CapabilityCaller? ToCapabilityCaller(this Caller? caller)
    {
        if (caller is null)
        {
            return null;
        }
        var brain = caller.Brain.Value;
        return caller.Actor switch
        {
            Actor.Person p => new CapabilityCaller(p.Principal, p.Account, brain, ConversationId: caller.Conversation),
            Actor.Agent ag => new CapabilityCaller(ag.For.Principal, ag.For.Account, brain, CapabilityCallers.AssistantApp, caller.Conversation),
            Actor.App ap => new CapabilityCaller(ap.For.Principal, ap.For.Account, brain, ap.Key, caller.Conversation),
            Actor.Module m => new CapabilityCaller(m.Id, m.Id, brain, m.Id, caller.Conversation),
            Actor.Scheduler => new CapabilityCaller(brain, brain, brain, CapabilityCallers.SchedulerApp, caller.Conversation),
            Actor.Kernel => new CapabilityCaller(brain, brain, brain, CapabilityCallers.PlatformApp, caller.Conversation),
            _ => new CapabilityCaller(brain, brain, brain, ConversationId: caller.Conversation)
        };
    }
}

public static class InProcessCallerScope
{
    private static readonly MethodInfo EnterMethod = Type.GetType("DigitalBrain.Runtime.Orleans.Access.InProcessCallerScope, DigitalBrain.Runtime.Orleans")
        ?.GetMethod("Enter", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InProcessCallerScope.Enter not found.");

    private static readonly object AmbientInstance = Type.GetType("DigitalBrain.Runtime.Orleans.Access.OrleansCallerAmbient, DigitalBrain.Runtime.Orleans")
        ?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetValue(null)
        ?? throw new InvalidOperationException("OrleansCallerAmbient.Instance not found.");

    private static readonly MethodInfo EnterAmbientMethod = AmbientInstance.GetType()
        .GetMethod("Enter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("OrleansCallerAmbient.Enter not found.");

    private static readonly string[] RequestContextKeys =
    [
        DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.RequestContextKey,
        "db.cap.account",
        "db.cap.brain",
        "db.cap.app",
        "db.cap.conversation",
    ];

    public static IDisposable Enter() => (IDisposable)EnterMethod.Invoke(null, null)!;

    public static IDisposable ClearCaller()
    {
        var saved = new object?[RequestContextKeys.Length];
        for (var i = 0; i < RequestContextKeys.Length; i++)
        {
            saved[i] = Orleans.Runtime.RequestContext.Get(RequestContextKeys[i]);
            Orleans.Runtime.RequestContext.Remove(RequestContextKeys[i]);
        }
        var inProcess = Enter();
        var ambient = (IDisposable)EnterAmbientMethod.Invoke(AmbientInstance, [null])!;
        return new ClearScope(ambient, inProcess, saved);
    }

    public static IDisposable EnterKernel(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var platform = DigitalBrain.Capabilities.CapabilityCallers.Platform(brainId, brainId, brainId);
        var inProcess = Enter();
        var stamped = DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(platform);
        var ambient = (IDisposable)EnterAmbientMethod.Invoke(
            AmbientInstance,
            [platform.ToCaller()])!;
        return new KernelScope(ambient, stamped, inProcess);
    }

    public static IDisposable EnterApp(string brainId, string appKey, string? accountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        var resolvedAccountId = string.IsNullOrWhiteSpace(accountId) ? brainId : accountId;
        var app = CapabilityCaller.App(appKey) with
        {
            AccountId = resolvedAccountId,
            BrainId = brainId,
        };
        var inProcess = Enter();
        var stamped = DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(app);
        var ambient = (IDisposable)EnterAmbientMethod.Invoke(
            AmbientInstance,
            [app.ToCaller()])!;
        return new KernelScope(ambient, stamped, inProcess);
    }

    private sealed class KernelScope(IDisposable first, IDisposable second, IDisposable third) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
            third.Dispose();
        }
    }

    private sealed class ClearScope(IDisposable ambient, IDisposable inProcess, object?[] saved) : IDisposable
    {
        public void Dispose()
        {
            ambient.Dispose();
            inProcess.Dispose();
            for (var i = 0; i < RequestContextKeys.Length; i++)
            {
                if (saved[i] is { } value)
                {
                    Orleans.Runtime.RequestContext.Set(RequestContextKeys[i], value);
                }
                else
                {
                    Orleans.Runtime.RequestContext.Remove(RequestContextKeys[i]);
                }
            }
        }
    }
}
