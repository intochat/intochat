#pragma warning disable xUnit1051

using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Contacts;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class EdgeGrantsCacheUsersetFacts
{
    [Fact]
    public async Task MemberOfListSatisfiesUsersetGrant()
    {
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        os.Register<ContactsNeuron>();

        var contacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("vlad"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));
        var grants = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("vlad"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));

        await contacts.Add("friends", new ContactEntry { Principal = "brain:bob" });
        await grants.Grant(Grant.Tuple("brain:vlad", IContactsNeuron.Userset("friends"), "vlad/apps/photocraft", GrantRelations.Run));

        var cache = new EdgeGrantsCache(services: os.Services, listeners: os.ClusterListeners, clock: TimeProvider.System);
        var bob = CapabilityCallers.Of(AppPrincipal.Id("gallery"), "bob-account", "bob", CallerKind.App, "gallery");

        Assert.True(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));
    }

    [Fact]
    public async Task RemovingMemberInvalidatesEdgeCacheForOwnerBrain()
    {
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        os.Register<ContactsNeuron>();

        var contacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("vlad"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));
        var grants = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("vlad"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));

        await contacts.Add("friends", new ContactEntry { Principal = "brain:bob" });
        await grants.Grant(Grant.Tuple("brain:vlad", IContactsNeuron.Userset("friends"), "vlad/apps/photocraft", GrantRelations.Run));

        var cache = new EdgeGrantsCache(services: os.Services, listeners: os.ClusterListeners, clock: TimeProvider.System);
        var bob = CapabilityCallers.Of(AppPrincipal.Id("gallery"), "bob-account", "bob", CallerKind.App, "gallery");

        Assert.True(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));

        await contacts.Remove("friends", "brain:bob");

        Assert.False(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));
    }

    [Fact]
    public async Task ListsInCallerBrainDoNotCount()
    {
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        os.Register<ContactsNeuron>();

        var bobContacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("bob"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));
        var vladGrants = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("vlad"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));

        // bob adds vlad to bob's own "friends"
        await bobContacts.Add("friends", new ContactEntry { Principal = "brain:vlad" });
        // vlad has a grant to contacts/friends#member
        await vladGrants.Grant(Grant.Tuple("brain:vlad", IContactsNeuron.Userset("friends"), "vlad/apps/photocraft", GrantRelations.Run));

        var cache = new EdgeGrantsCache(services: os.Services, listeners: os.ClusterListeners, clock: TimeProvider.System);
        var bob = CapabilityCallers.Of(AppPrincipal.Id("gallery"), "bob-account", "bob", CallerKind.App, "gallery");

        // bob is NOT in vlad's friends list, so must be false
        Assert.False(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));
    }
}
