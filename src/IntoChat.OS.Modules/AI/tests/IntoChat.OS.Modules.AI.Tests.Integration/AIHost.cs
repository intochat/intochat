using IntoChat.OS.AI;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.AI.Tests.Integration.AIHost))]

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The shared host composes the AI module with no provider and no model. Facts that script a fake model or a chat
// client, configure providers or AI options, or compose other modules keep their own TestBrain ([Trait("host", "own")]);
// those settings are host-global.
public sealed class AIHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<AIModule>();
}
