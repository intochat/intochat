// os-only: Bridges legacy CapabilityCaller to 0.19.0 DigitalBrain Caller
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

    public static IDisposable Enter() => (IDisposable)EnterMethod.Invoke(null, null)!;
}
