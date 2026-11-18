using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Contacts;

namespace IntoChat.OS.Authority;

public sealed class ContactsNeuronFacts
{
    private static async Task<(TestOS, IContactsNeuron)> Open()
    {
        var os = TestOS.Local();
        os.Register<ContactsNeuron>();
        return (os, os.Get<IContactsNeuron>(new NeuronId(new BrainId("vlad"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey)));
    }

    [Fact]
    public async Task AddCreatesListAndIsMemberReflectsIt()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("vlad", "vlad", new BrainId("vlad")));
        var (os, c) = await Open();
        await using var _ = os;
        await c.Add("friends", new ContactEntry { Principal = "brain:bob", Handle = "bob" });
        Assert.True(await c.IsMember("friends", "brain:bob"));
        Assert.False(await c.IsMember("family", "brain:bob"));
        Assert.Equal(["friends"], await c.ListsContaining("brain:bob"));
        Assert.Equal(1, await c.GetVersion());
    }

    [Fact]
    public async Task RemoveDropsMemberAndBumpsVersionOnce()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("vlad", "vlad", new BrainId("vlad")));
        var (os, c) = await Open();
        await using var _ = os;
        await c.Add("friends", new ContactEntry { Principal = "brain:bob" });
        await c.Remove("friends", "brain:bob");
        await c.Remove("friends", "brain:bob");
        Assert.False(await c.IsMember("friends", "brain:bob"));
        Assert.Equal(2, await c.GetVersion());
    }

    [Fact]
    public async Task InvalidListNameIsRejected()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("vlad", "vlad", new BrainId("vlad")));
        var (os, c) = await Open();
        await using var _ = os;
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => c.Add("My Friends", new ContactEntry { Principal = "brain:bob" }));
        Assert.Contains("identity.contact-list-name-valid", ex.Rejected.Rules);
    }

    [Fact]
    public void UsersetStringShape() => Assert.Equal("contacts/friends#member", IContactsNeuron.Userset("friends"));

    [Fact]
    public async Task AddReplacesMemberWithSamePrincipal()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("vlad", "vlad", new BrainId("vlad")));
        var (os, c) = await Open();
        await using var _ = os;
        await c.Add("friends", new ContactEntry { Principal = "brain:bob", Handle = "bob1" });
        await c.Add("friends", new ContactEntry { Principal = "brain:bob", Handle = "bob2" });
        var lists = await c.Lists();
        var friends = Assert.Single(lists, l => l.Name == "friends");
        var member = Assert.Single(friends.Members);
        Assert.Equal("bob2", member.Handle);
        Assert.Equal(2, await c.GetVersion());
    }
}
