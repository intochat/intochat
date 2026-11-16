using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework;

internal readonly record struct CallerIdentity(
    string PrincipalId,
    string AccountId,
    string BrainId,
    CallerKind Kind,
    string? AppId,
    string? ConversationId,
    TrustedEdge StampedBy,
    bool IsModule)
{
    public string PersonId => Kind == CallerKind.App ? AccountId : PrincipalId;

    public static CallerIdentity From(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var brain = caller.Brain.Value;
        return caller.Actor switch
        {
            Actor.Person person => new(person.Principal, person.Account, brain, CallerKind.User, null, caller.Conversation, TrustedEdge.AuthenticatedHttp, false),
            Actor.Agent agent => new(agent.For.Principal, agent.For.Account, brain, CallerKind.Agent, CapabilityCallers.AssistantApp, caller.Conversation, TrustedEdge.AuthenticatedHttp, false),
            Actor.App app => new(app.For.Principal, app.For.Account, brain, CallerKind.App, app.Key, caller.Conversation, TrustedEdge.AppProxy, false),
            Actor.Module module => new(module.Id, module.Id, brain, CallerKind.Platform, null, caller.Conversation, TrustedEdge.Platform, true),
            Actor.Scheduler => new(brain, brain, brain, CallerKind.Scheduler, CapabilityCallers.SchedulerApp, caller.Conversation, TrustedEdge.Scheduler, false),
            Actor.Kernel => new(brain, brain, brain, CallerKind.Platform, CapabilityCallers.PlatformApp, caller.Conversation, TrustedEdge.Platform, false),
            _ => new(brain, brain, brain, CallerKind.User, null, caller.Conversation, TrustedEdge.AuthenticatedHttp, false),
        };
    }

    public static CallerIdentity From(CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new(
            caller.PrincipalId,
            caller.AccountId,
            caller.BrainId,
            caller.Kind,
            caller.AppId,
            caller.ConversationId,
            caller.StampedBy,
            false);
    }

    public CapabilityCaller ToCapabilityCaller()
        => new(PrincipalId, AccountId, BrainId, AppId, ConversationId);
}
