using System.Reflection;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules;

// One caller rule (Neuron.RequireCaller / RequireSameBrain): a neuron never decides on a missing caller. These guards used to
// treat a call with no caller as the owner's; the access gate refuses such a call first, so each is reached here directly on
// the neuron, as a path that skips the gate would reach it.
public sealed class CallerRuleFacts
{
    private static readonly BrainId Brain = new("ada");
    private static readonly BrainId Stranger = new("bob");

    public sealed record Site(string Assembly, string Class, string Type, string Key, string Method, Func<object?[]> Arguments)
    {
        public override string ToString() => $"{Class.Split('.')[^1]}.{Method}";
    }

    private static readonly Site[] Sites =
    [
        new("IntoChat.OS.Modules.Catalog", "IntoChat.OS.Modules.Apps.Social.PostsNeuron", "social.posts", "default", "Publish", () => [new PostDraft("hello", []), CancellationToken.None]),
        new("IntoChat.OS.Modules.Catalog", "IntoChat.OS.Modules.Apps.Social.RoomNeuron", "social.room", "hall", "Pin", () => ["app.notes", "default", CancellationToken.None]),
        new("IntoChat.OS.Modules.Catalog", "IntoChat.OS.Modules.Apps.Social.RoomNeuron", "social.room", "hall", "Invite", () => ["bob", Stranger, Role.Editor, CancellationToken.None]),
        new("IntoChat.OS.Modules.AI", "IntoChat.OS.Modules.AI.Conversations.ConversationNeuron", "conversation", "c1", "Post", () => ["hello", "e1"]),
        new("IntoChat.OS.Modules.AI", "IntoChat.OS.Modules.AI.Conversations.ConversationNeuron", "conversation", "c1", "Stop", () => []),
        new("IntoChat.OS.Modules.Catalog", "IntoChat.OS.Modules.Apps.FriendsNeuron", "friends", "default", "List", () => []),
        new("IntoChat.OS.Modules.Catalog", "IntoChat.OS.Modules.Apps.FriendsNeuron", "friends", "default", "IsFriend", () => [new AccountId("bob")]),
        new("IntoChat.OS.Modules.Postgres", "IntoChat.OS.Modules.Postgres.Tables.TableNeuron", "postgres.table", "ada/app/notes", "Define", () => [new TableDefinition([new TableColumn("id", "text")], ["id"])]),
        new("IntoChat.OS.Modules.Postgres", "IntoChat.OS.Modules.Postgres.Tables.TableNeuron", "postgres.table", "ada/app/notes", "Retire", () => ["ada/app/notes"]),
        new("IntoChat.OS.Modules.Shell", "IntoChat.OS.Modules.Flutter.Viewport.ViewportNeuron", "viewport", "apps/notes/main", "Set", () => [null, 0L]),
        new("IntoChat.OS.Modules.Catalog", "IntoChat.OS.Modules.Apps.Approvals.ApprovalsNeuron", "approvals", "default", "Pending", () => [CancellationToken.None]),
        new("IntoChat.OS.Modules.Circles", "IntoChat.OS.Modules.Circles.LegacyCircleIndexNeuron", "circles.factory", "default", "Circles", () => []),
    ];

    public static TheoryData<Site> All => [.. Sites];

    [Theory]
    [MemberData(nameof(All))]
    public async Task A_call_with_no_caller_is_refused(Site site)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call(site, caller: null));
    }

    [Theory]
    [MemberData(nameof(All))]
    public async Task A_caller_of_another_brain_is_refused(Site site)
    {
        // IsFriend admits the named friend and nobody else of another brain.
        var stranger = TestEdge.Authority.Person("carol", "carol", Stranger);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call(site, stranger));
    }

    private static async Task Call(Site site, Caller? caller)
    {
        var type = System.Reflection.Assembly.Load(site.Assembly).GetType(site.Class, throwOnError: true)!;
        var constructor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var neuron = (Neuron)constructor.Invoke(new object?[constructor.GetParameters().Length]);
        var context = new FakeNeuronContext(new NeuronId(Brain, site.Type, site.Key)) { Caller = caller };
        NeuronAttachment.Attach(neuron, context);
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == site.Method && m.GetParameters().Length == site.Arguments().Length);
        try
        {
            await (Task)method.Invoke(neuron, site.Arguments())!;
        }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            throw inner;
        }
    }
}
