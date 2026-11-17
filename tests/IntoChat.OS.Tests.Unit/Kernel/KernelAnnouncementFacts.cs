using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Brain;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Composition;

namespace IntoChat.OS.Tests.Unit.Kernel;

// Brain announcements are kernel-only. The kernel grains used to clear the caller, so the brain refused them silently.
public sealed class KernelAnnouncementFacts
{
    [Fact]
    public async Task AnAnnouncementLandsAsTheKernelOfItsBrain()
    {
        await using var os = TestOS.Local();
        var announcer = new Recording(os.Callers);
        var person = os.Edge.Person("alice", "alice", new BrainId("alice"));

        using (os.Edge.Enter(person))
        {
            KernelAnnouncement.Send(os.Edge, new BrainId("alice"), () => announcer, brain => brain.InstanceCreated("notes", "alice/n1"));
            // The person's frame is back once the announcement is sent.
            Assert.Same(person, os.Callers.Current);
        }

        var (call, caller) = Assert.Single(announcer.Calls);
        Assert.Equal("InstanceCreated notes alice/n1", call);
        Assert.IsType<Actor.Kernel>(caller?.Actor);
        Assert.Equal(new BrainId("alice"), caller!.Brain);
    }

    private sealed class Recording(ICallerAmbient callers) : IBrainAnnouncer
    {
        public List<(string Call, Caller? Caller)> Calls { get; } = [];

        public Task InstanceCreated(string type, string key) => Record($"InstanceCreated {type} {key}");

        public Task InstanceRetired(string type, string key) => Record($"InstanceRetired {type} {key}");

        public Task TypeAdded(string name) => Record($"TypeAdded {name}");

        private Task Record(string call)
        {
            Calls.Add((call, callers.Current));
            return Task.CompletedTask;
        }
    }
}
