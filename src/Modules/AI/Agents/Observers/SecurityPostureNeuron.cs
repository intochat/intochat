using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Access;
using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.AI.Agents.Observers;

[GenerateSerializer, Alias("ai.security-posture.state")]
internal sealed record SecurityPostureState
{
    // The assistant's grants seen in force at the last read: one that is gone from the next read was revoked (or ran out).
    [Id(0)] public string[] Held { get; init; } = [];

    [Id(1)] public bool Seeded { get; init; }
}

[Neuron("ai.security-posture", State = "ai-security-posture")]
internal sealed class SecurityPostureNeuron(EdgeAuthority? edge = null) : AiNeuron<SecurityPostureState>, ISecurityPosture
{
    private NeuronId GrantsId => new(Id.Brain, "grants.default", "grants/default");

    private IAgentContext Posture => Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", "context/security.posture"));

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Wires.Ensure<GrantsChanged>(GrantsId);
        // The first read only learns what is held; nothing was lost yet.
        if (!Snapshot.Seeded) { await Read(announce: false); }
    }

    [On]
    public Task OnGrantsChanged(GrantsChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return Read(announce: true);
    }

    public async Task Stop(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using var _ = Platform();
        await Posture.Set(new ContextEntry("stop", reason, [], [ContextTags.Interrupt]));
    }

    public async Task Resume()
    {
        using var _ = Platform();
        await Posture.Remove("stop");
    }

    private async Task Read(bool announce)
    {
        var grants = await Get<IGrantsNeuron>(GrantsId).List();
        var assistant = CallerApps.Assistant;
        var held = grants.Where(grant => !grant.Revoked && (grant.AppId == assistant || grant.Grantee == "app:" + assistant)).Select(grant => grant.Id).ToArray();
        var lost = Snapshot.Held.Except(held, StringComparer.Ordinal).Any();
        await Save(new SecurityPostureState { Held = held, Seeded = true }, new SecurityPostureNoted(FullKey, held.Length));
        if (!announce || !lost) { return; }
        using var _ = Platform();
        await Posture.Set(new ContextEntry("posture", "A grant for the assistant was revoked.", []));
    }

    // The posture writes the brain's interrupt as the AI security module acting as itself, which context reads as platform trust.
    private IDisposable Platform() => edge is null
        ? throw new InvalidOperationException("The security posture needs the edge authority to act as its module.")
        : edge.Enter(edge.Module("ai.security", Id.Brain));
}
