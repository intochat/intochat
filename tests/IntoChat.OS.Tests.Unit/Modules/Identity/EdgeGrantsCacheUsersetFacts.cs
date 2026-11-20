#pragma warning disable xUnit1051

using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Identity.Contacts;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Contracts.Contacts;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class EdgeGrantsCacheUsersetFacts
{
    [Fact]
    public async Task MemberOfListSatisfiesUsersetGrant()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("vlad", "vlad", new BrainId("vlad")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        os.Register<ContactsNeuron>();

        var contacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("vlad"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));
        var grants = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("vlad"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));

        await contacts.Add("friends", new ContactEntry { Principal = "brain:bob" });
        await grants.Grant(Grant.Tuple("brain:vlad", IContactsNeuron.Userset("friends"), "vlad/apps/photocraft", GrantRelations.Run));

        var cache = new EdgeGrantsCache(
            services: IntoChat.OS.Tests.TestEdge.WithEdge(os.Services),
            listeners: os.ClusterListeners,
            clock: TimeProvider.System,
            resolveNeuron: Resolve(os));
        var bob = IntoChat.OS.Tests.TestEdge.Authority.App("gallery", new Actor.Person("bob-account", "bob-account"), new BrainId("bob"));

        Assert.True(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));
    }

    [Fact]
    public async Task RemovingMemberInvalidatesEdgeCacheForOwnerBrain()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("vlad", "vlad", new BrainId("vlad")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        os.Register<ContactsNeuron>();

        var contacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("vlad"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));
        var grants = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("vlad"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));

        await contacts.Add("friends", new ContactEntry { Principal = "brain:bob" });
        await grants.Grant(Grant.Tuple("brain:vlad", IContactsNeuron.Userset("friends"), "vlad/apps/photocraft", GrantRelations.Run));

        var cache = new EdgeGrantsCache(
            services: IntoChat.OS.Tests.TestEdge.WithEdge(os.Services),
            listeners: os.ClusterListeners,
            clock: TimeProvider.System,
            resolveNeuron: Resolve(os));
        var bob = IntoChat.OS.Tests.TestEdge.Authority.App("gallery", new Actor.Person("bob-account", "bob-account"), new BrainId("bob"));

        Assert.True(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));

        await contacts.Remove("friends", "brain:bob");

        Assert.False(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));
    }

    [Fact]
    public async Task ListsInCallerBrainDoNotCount()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("bob", "bob", new BrainId("bob")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        os.Register<ContactsNeuron>();

        var bobContacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("bob"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));
        var vladGrants = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("vlad"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));

        // bob adds vlad to bob's own "friends"
        await bobContacts.Add("friends", new ContactEntry { Principal = "brain:vlad" });
        // vlad has a grant to contacts/friends#member
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("vlad", "vlad", new BrainId("vlad"))))
        {
            await vladGrants.Grant(Grant.Tuple("brain:vlad", IContactsNeuron.Userset("friends"), "vlad/apps/photocraft", GrantRelations.Run));
        }

        var cache = new EdgeGrantsCache(
            services: IntoChat.OS.Tests.TestEdge.WithEdge(os.Services),
            listeners: os.ClusterListeners,
            clock: TimeProvider.System,
            resolveNeuron: Resolve(os));
        var bob = IntoChat.OS.Tests.TestEdge.Authority.App("gallery", new Actor.Person("bob-account", "bob-account"), new BrainId("bob"));

        // bob is NOT in vlad's friends list, so must be false
        Assert.False(await cache.IsGrantedAsync("vlad", bob, GrantRelations.Run, "vlad/apps/photocraft"));
    }

    private static Func<Type, NeuronId, object?> Resolve(TestOS os) => (type, id) =>
        type == typeof(IGrantsNeuron) ? os.Get<IGrantsNeuron>(id) :
        type == typeof(IContactsNeuron) ? os.Get<IContactsNeuron>(id) :
        null;
}
