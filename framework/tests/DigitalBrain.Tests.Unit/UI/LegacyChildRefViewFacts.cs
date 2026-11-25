using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Tests.Unit.Neurons;
using DigitalBrain.Tests.Unit.Screens;
using DigitalBrain.UI;
using DigitalBrain.Units;

namespace DigitalBrain.Tests.Unit.UI;

// A view stored before a screen's mounted control was a "child" names it with refKind "slot". A host reads it as "child" when it loads
// the view, so no client is ever served the retired word.
public sealed class LegacyChildRefViewFacts
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task A_screen_view_stored_with_a_slot_ref_is_served_as_a_child_ref()
    {
        var stored = new PublishedView("app.sample-counter", 4, Parse(
            """{"title":"Sample Counter","root":{"kind":"column","children":[{"kind":"neuronRef","refKind":"slot","neuronUri":"alice/app.sample-counter/default/editor"}]}}"""));
        var screen = new SampleCounterScreen();
        var context = new RecordingContext(new NeuronId(new BrainId("alice"), "app.sample-counter", "default"));
        context.Store(JsonSerializer.SerializeToUtf8Bytes(new StoredState<SampleCounterState>(new SampleCounterState { Count = 1 }, stored),
            RuntimeProtocol.OptionsFor(typeof(StoredState<SampleCounterState>))));
        NeuronAttachment.Attach(screen, context);
        await NeuronAttachment.Activate(screen, CancellationToken.None);

        var served = (await screen.Read())!.View.GetRawText();

        Assert.Contains("\"refKind\":\"child\"", served, StringComparison.Ordinal);
        Assert.DoesNotContain("\"slot\"", served, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slot_ref_at_any_depth_reads_as_a_child_ref_and_other_ref_kinds_and_payloads_stay()
    {
        var view = Parse(
            """{"root":{"kind":"column","children":[{"kind":"neuronRef","refKind":"slot","neuronUri":"a/b/c/d"},{"kind":"neuronRef","refKind":"view","neuronUri":"a/apps/x/view"},{"kind":"button","key":"k","command":{"target":".","signal":"s","args":{"refKind":"slot"}}}]}}""");

        var normalized = LegacyViewJson.Normalize(view).GetProperty("root").GetProperty("children");

        Assert.Equal("child", normalized[0].GetProperty("refKind").GetString());
        Assert.Equal("view", normalized[1].GetProperty("refKind").GetString());
        Assert.Equal("slot", normalized[2].GetProperty("command").GetProperty("args").GetProperty("refKind").GetString());
    }

    [Fact]
    public void A_view_with_no_slot_ref_is_the_same_element()
    {
        var view = Parse("""{"root":{"kind":"neuronRef","refKind":"child","neuronUri":"a/b/c/d"}}""");

        Assert.Equal(view.GetRawText(), LegacyViewJson.Normalize(view).GetRawText());
    }
}
