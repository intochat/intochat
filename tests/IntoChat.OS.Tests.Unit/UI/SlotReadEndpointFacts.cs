using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Flutter;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IntoChat.OS.UI;

public sealed record SlotReadState
{
    public int Count { get; init; }
}

// The app "slotread": its screen shows slot "panel", whose screen shows slot "leaf".
[Neuron("app.slotread")]
public sealed class SlotReadAppScreen : Screen<SlotReadState>
{
    protected override View Render(SlotReadState state) => new("Slot read", new Column([new Heading("Top"), Slot<SlotReadPanelScreen>("panel")]));
}

[Neuron("test.slotread-panel")]
public sealed class SlotReadPanelScreen : Screen<SlotReadState>
{
    protected override View Render(SlotReadState state) => new("Panel", new Column([new Heading("Panel text"), Slot<SlotReadLeafScreen>("leaf")]));
}

[Neuron("test.slotread-leaf")]
public sealed class SlotReadLeafScreen : Screen<SlotReadState>
{
    protected override View Render(SlotReadState state) => new("Leaf", new Heading("Leaf text"));
}

// GET /brains/{id}/apps/{name}/slots/{path}: a slot child is read through its app's top-level screen and slot table, by key path.
public sealed class SlotReadEndpointFacts
{
    private const string Scope = "owner";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Caller? Alice => IntoChat.OS.Tests.TestEdge.Authority.Current;

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId(Scope)));

    private sealed class Proxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }

    private static async Task<(TestOS Os, INeuronProxies Brain)> Start()
    {
        var os = TestOS.Local().Register<SlotReadAppScreen>().Register<SlotReadPanelScreen>().Register<SlotReadLeafScreen>();
        var top = os.Get<IScreen>(new NeuronId(new BrainId(Scope), "app.slotread", "default"));
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId(Scope)))) { await top.Open(); }
        await os.Idle();
        return (os, new Proxies(os));
    }

    private static async Task<(int Status, string Body)> Read(INeuronProxies brain, string name, string path, string scope = Scope)
    {
        var result = await AppUiEndpoints.ReadSlot(name, path, brain, scope, Ct, Alice);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return (status, JsonSerializer.Serialize(value, AppUiEndpoints.ViewJson));
    }

    [Fact]
    public async Task The_owner_reads_a_slot_and_a_nested_slot_by_key_path()
    {
        using var _ = AsPerson();
        var (os, brain) = await Start();
        await using var __ = os;

        var panel = await Read(brain, "slotread", "panel");
        var leaf = await Read(brain, "slotread", "panel/leaf");

        Assert.Equal(200, panel.Status);
        Assert.Contains("Panel text", panel.Body, StringComparison.Ordinal);
        Assert.Contains("\"neuronUri\":\"leaf\"", panel.Body, StringComparison.Ordinal);
        Assert.Equal(200, leaf.Status);
        Assert.Contains("Leaf text", leaf.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_response_carries_a_raw_child_neuron_id()
    {
        using var _ = AsPerson();
        var (os, brain) = await Start();
        await using var __ = os;

        foreach (var path in new[] { "panel", "panel/leaf" })
        {
            var (_, body) = await Read(brain, "slotread", path);
            // A child's id is "<brain>/<type>/<parent id>/<key>": neither the parent's id nor the brain prefix may appear.
            Assert.DoesNotContain("app.slotread/default", body, StringComparison.Ordinal);
            Assert.DoesNotContain("owner/", body, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("owner/", JsonSerializer.Serialize(SlotKeys.Hide(await brain.Get<IScreen>(new NeuronId(new BrainId(Scope), "app.slotread", "default")).Read())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_key_is_404_UnknownChild()
    {
        using var _ = AsPerson();
        var (os, brain) = await Start();
        await using var __ = os;

        var (status, body) = await Read(brain, "slotread", "panel/ghost");

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Contains(ActivationRefusedException.UnknownChild, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("panel/../panel", StatusCodes.Status400BadRequest)]
    [InlineData("panel//leaf", StatusCodes.Status400BadRequest)]
    [InlineData("./panel", StatusCodes.Status400BadRequest)]
    [InlineData("owner:test.slotread-panel", StatusCodes.Status400BadRequest)]
    [InlineData("owner/test.slotread-panel/owner/app.slotread/default/panel", StatusCodes.Status404NotFound)]
    public async Task A_dot_dot_empty_or_raw_id_path_is_refused(string path, int expected)
    {
        using var _ = AsPerson();
        var (os, brain) = await Start();
        await using var __ = os;

        var (status, body) = await Read(brain, "slotread", path);

        Assert.Equal(expected, status);
        Assert.DoesNotContain("Panel text", body, StringComparison.Ordinal);
    }

    private sealed class Unloaded : IntoChat.OS.Host.Kernel.IHostedAppTypes
    {
        public bool IsHostLoaded(string appKey) => false;
    }

    [Fact]
    public async Task An_app_whose_host_is_not_loaded_is_409_NotLoaded()
    {
        using var _ = AsPerson();
        var (os, brain) = await Start();
        await using var __ = os;

        var result = await AppUiEndpoints.ReadSlot("slotread", "panel", brain, Scope, Ct, Alice, new Unloaded());

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("NotLoaded", JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_from_another_brain_is_refused_under_this_brains_scope()
    {
        var (os, brain) = await Start();
        await using var __ = os;
        using var _ = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("bob", "bob", new BrainId("other")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => AppUiEndpoints.ReadSlot("slotread", "panel", brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
    }

    [Fact]
    public async Task Another_brains_app_or_a_raw_name_is_refused()
    {
        using var _ = AsPerson();
        var (os, brain) = await Start();
        await using var __ = os;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.ReadSlot("slotread", "panel", brain, "other", Ct, Alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.ReadSlot("slotread/view", "panel", brain, Scope, Ct, Alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.ReadSlot("slotread", "panel", brain, Scope, Ct, caller: null));
    }
}

// Views name slots by key; a raw id that cannot be keyed never passes through.
public sealed class SlotKeysFacts
{
    [Theory]
    [InlineData("owner/control.listBox/owner/app.uikit/default/nav", "nav")]
    [InlineData("owner/units/bank", "owner/units/bank")]
    [InlineData("owner/control.listBox/main", "")]
    [InlineData("not an id", "")]
    public void A_slot_is_named_by_its_key_or_dropped(string uri, string expected)
    {
        var view = JsonSerializer.SerializeToElement(new { root = new { kind = "neuronRef", refKind = "slot", neuronUri = uri } });

        Assert.Equal(expected, SlotKeys.Hide(view).GetProperty("root").GetProperty("neuronUri").GetString());
    }
}

// The server marks a screen's view for the act route; a Kit app's view (an IView) carries no mark. The slot route maps a refusal
// other than UnknownChild to 409 with its reason and a failed read to 422.
public sealed class ScreenViewMarkFacts
{
    private const string Scope = "owner";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId(Scope)));

    private sealed class Proxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }

    private sealed class KitView(JsonElement? raw = null) : IntoChat.OS.Modules.Flutter.Views.IView
    {
        public Task Publish(long generation, View view) => Task.CompletedTask;
        public Task<PublishedView?> Read() => Task.FromResult<PublishedView?>(new("kit", 1,
            raw ?? JsonSerializer.SerializeToElement(new View("Kit", new Heading("Kit text")), JsonSerializerOptions.Web)));
        public Task<IntoChat.OS.Modules.Flutter.Views.ViewActivation> Activate(long generation, string key, string? fieldValue)
            => throw new NotSupportedException();
    }

    // Only the Kit app's IView answers; every other neuron is missing.
    private sealed class KitBrain(JsonElement? raw = null) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class
            => typeof(T) == typeof(IntoChat.OS.Modules.Flutter.Views.IView) ? (T)(object)new KitView(raw) : throw new InvalidOperationException($"No neuron {id}.");
    }

    private static string Body(IResult result) => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value, AppUiEndpoints.ViewJson);

    [Fact]
    public async Task A_screen_apps_view_is_marked_for_the_act_route()
    {
        using var _ = AsPerson();
        await using var os = TestOS.Local().Register<SlotReadAppScreen>().Register<SlotReadPanelScreen>().Register<SlotReadLeafScreen>();
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId(Scope))))
        {
            await os.Get<IScreen>(new NeuronId(new BrainId(Scope), "app.slotread", "default")).Open();
        }
        await os.Idle();

        var view = Body(await AppUiEndpoints.ReadNode("view", "owner/apps/slotread/view", new Proxies(os), Scope, Ct));
        var slot = Body(await AppUiEndpoints.ReadSlot("slotread", "panel", new Proxies(os), Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));

        Assert.Contains("\"acts\":\"act\"", view, StringComparison.Ordinal);
        Assert.Contains("\"acts\":\"act\"", slot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kit_apps_view_is_not_marked()
    {
        var body = Body(await AppUiEndpoints.ReadNode("view", "owner/apps/creator/view", new KitBrain(), Scope, Ct));

        Assert.Contains("Kit text", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"acts\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kit_apps_view_that_sets_the_mark_itself_comes_out_unmarked()
    {
        var forged = JsonSerializer.SerializeToElement(new { title = "Kit", root = new { kind = "heading", text = "Kit text" }, acts = View.ActRoute });

        var body = Body(await AppUiEndpoints.ReadNode("view", "owner/apps/creator/view", new KitBrain(forged), Scope, Ct));

        Assert.Contains("Kit text", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"acts\"", body, StringComparison.Ordinal);
    }

    private sealed class FailingScreen(Exception error) : IScreen
    {
        public Task<PublishedView?> Open() => Task.FromResult<PublishedView?>(null);
        public Task Act(ScreenAction action) => Task.CompletedTask;
        public Task<PublishedView?> Read(string? alias = null) => Task.FromResult<PublishedView?>(null);
        public Task Activate(string alias, long generation, string nodeKey, string? fieldValue = null, string? by = null) => Task.CompletedTask;
        public Task Request(string signal, JsonElement? args = null) => Task.CompletedTask;
        public Task Dispatch(Command command) => Task.FromException(error);
        public Task<PublishedView?> ReadSlot(string path) => Task.FromException<PublishedView?>(error);
    }

    private sealed class ScreenBrain(IScreen screen) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => (T)screen;
    }

    [Fact]
    public async Task A_refusal_other_than_UnknownChild_is_409_with_its_reason()
    {
        using var _ = AsPerson();
        var brain = new ScreenBrain(new FailingScreen(new ActivationRefusedException(ActivationRefusedException.Stale)));

        var result = await AppUiEndpoints.ReadSlot("slotread", "panel", brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains(ActivationRefusedException.Stale, Body(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_read_is_422_HandlerFailed()
    {
        using var _ = AsPerson();
        var brain = new ScreenBrain(new FailingScreen(new InvalidOperationException("the panel broke")));

        var result = await AppUiEndpoints.ReadSlot("slotread", "panel", brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("HandlerFailed", Body(result), StringComparison.Ordinal);
    }
    [Fact]
    public async Task A_read_whose_handler_throws_ArgumentException_is_422_HandlerFailed()
    {
        using var _ = AsPerson();
        var brain = new ScreenBrain(new FailingScreen(new ArgumentException("the panel's handler broke")));

        var result = await AppUiEndpoints.ReadSlot("slotread", "panel", brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("HandlerFailed", Body(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_act_whose_handler_throws_ArgumentException_is_422_HandlerFailed()
    {
        using var _ = AsPerson();
        var brain = new ScreenBrain(new FailingScreen(new ArgumentException("the handler broke")));

        var result = await AppUiEndpoints.Act(new AppUiEndpoints.ActRequest("owner/apps/slotread", ".", "press"), brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("HandlerFailed", Body(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_slot_path_is_still_400()
    {
        using var _ = AsPerson();
        var brain = new ScreenBrain(new FailingScreen(new ArgumentException("unused")));

        var result = await AppUiEndpoints.ReadSlot("slotread", "a/../b", brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
