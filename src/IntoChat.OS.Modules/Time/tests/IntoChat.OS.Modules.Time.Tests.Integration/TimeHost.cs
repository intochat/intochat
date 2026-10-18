using IntoChat.OS.Time;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Time.Tests.Integration.TimeHost))]

namespace IntoChat.OS.Modules.Time.Tests.Integration;

// The shared host composes the Time module on real timers; its facts use timer keys no other shared fact uses. Facts
// that install controlled timers, reminders or fast collection keep their own TestBrain ([Trait("host", "own")]).
public sealed class TimeHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<TimeModule>();
}
