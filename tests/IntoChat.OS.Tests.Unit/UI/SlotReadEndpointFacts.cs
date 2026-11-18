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
    [InlineData("panel/../panel")]
    [InlineData("panel//leaf")]
    [InlineData("./panel")]
    [InlineData("owner/test.slotread-panel/owner/app.slotread/default/panel")]
    [InlineData("owner:test.slotread-panel")]
    public async Task A_dot_dot_empty_or_raw_id_path_is_refused(string path)
    {
        using var _ = AsPerson();
        var (os, brain) = await Start();
        await using var __ = os;

        var (status, body) = await Read(brain, "slotread", path);

        Assert.True(status is StatusCodes.Status400BadRequest or StatusCodes.Status404NotFound, $"{path} gave {status}");
        Assert.DoesNotContain("Panel text", body, StringComparison.Ordinal);
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
