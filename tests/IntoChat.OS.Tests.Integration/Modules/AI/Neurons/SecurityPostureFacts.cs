using IntoChat.OS.Tests;
using IntoChat.OS.Framework;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Observers;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The posture neuron writes the security.posture context as the platform, so its interrupt is honoured by the agents
// that list it. It reads and writes through the silo's grain factory: silo runtime only.
public sealed class SecurityPostureFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task StopWritesATrustedInterrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        using var person = runtime.As(Who.Person);

        await runtime.Get<ISecurityPosture>(runtime.Id("ai.security-posture", "security/posture")).Stop("breach suspected");

        var stop = Assert.Single((await Context(runtime).Read()).Entries, entry => entry.Name == "stop");
        Assert.Equal("breach suspected", stop.Text);
        Assert.Equal([ContextTags.Interrupt], stop.EffectiveTags);
        Assert.Equal(ContextTrust.Platform, stop.Provenance!.Trust);
        var updated = Assert.Single(await runtime.WaitForSignals<ContextUpdated>(ContextId(runtime), 1, Within, ct));
        Assert.False(updated.Ignored);
    }

    [Fact]
    public async Task ResumeRemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        using var person = runtime.As(Who.Person);
        var posture = runtime.Get<ISecurityPosture>(runtime.Id("ai.security-posture", "security/posture"));
        await posture.Stop("breach suspected");

        await posture.Resume();

        Assert.DoesNotContain((await Context(runtime).Read()).Entries, entry => entry.Name == "stop");
    }

    [Fact]
    public async Task ARevokedAssistantGrantIsNoted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        using var person = runtime.As(Who.Person);
        var postureId = runtime.Id("ai.security-posture", "security/posture");
        await runtime.Get<ISecurityPosture>(postureId).Resume(); // wakes the neuron, so its synapse on the grants exists
        var grants = runtime.Get<IGrantsNeuron>(runtime.Id(IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
        var assistant = CallerApps.Assistant;
        await grants.Grant(new Grant { Id = "g-assistant", AppId = assistant, SemanticTypeId = "person.birthDate", WorkspaceId = runtime.Brain.Value });
        // Revoking is only news once the neuron has seen the grant in force: the activation read, then this grant.
        await runtime.WaitForSignals<SecurityPostureNoted>(postureId, 2, Within, ct);

        await grants.Revoke("g-assistant");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Within);
        while (true)
        {
            var entries = (await Context(runtime).Read()).Entries;
            if (entries.FirstOrDefault(entry => entry.Name == "posture") is { } note)
            {
                Assert.Equal("A grant for the assistant was revoked.", note.Text);
                Assert.Empty(note.EffectiveTags);
                return;
            }
            await Task.Delay(50, limit.Token);
        }
    }

    private static NeuronId ContextId(INeuronRuntime runtime) => runtime.Id("agent-context", "context/security.posture");

    private static IAgentContext Context(INeuronRuntime runtime) => runtime.Get<IAgentContext>(ContextId(runtime));
}
