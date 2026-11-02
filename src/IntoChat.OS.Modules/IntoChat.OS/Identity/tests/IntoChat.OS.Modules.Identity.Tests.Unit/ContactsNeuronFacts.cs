using DigitalBrain;
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
        var (os, c) = await Open();
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => c.Add("My Friends", new ContactEntry { Principal = "brain:bob" }));
    }

    [Fact]
    public void UsersetStringShape() => Assert.Equal("contacts/friends#member", IContactsNeuron.Userset("friends"));

    [Fact]
    public async Task AddReplacesMemberWithSamePrincipal()
    {
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
