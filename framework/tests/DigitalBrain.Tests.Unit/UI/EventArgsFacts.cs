using System.Text.Json;
using DigitalBrain.UI;

namespace DigitalBrain.Tests.Unit.UI;

public sealed class EventArgsFacts
{
    [Fact]
    public void EventPayload_reads_typed_args_under_event()
    {
        using var doc = JsonDocument.Parse("""{"event":{"isOn":true}}""");

        var args = EventPayload.Read<ToggledArgs>(doc.RootElement.Clone());

        Assert.Equal(new ToggledArgs(true), args);
    }

    [Fact]
    public void EventPayload_missing_event_throws_with_signal_name()
    {
        using var doc = JsonDocument.Parse("""{"value":"x"}""");
        var root = doc.RootElement.Clone();

        var ex = Assert.Throws<InvalidOperationException>(() => EventPayload.Read<SubmitArgs>(root, "chat.submit"));

        Assert.Contains("chat.submit", ex.Message);
        Assert.Contains("event", ex.Message);
        Assert.Throws<InvalidOperationException>(() => EventPayload.Read<SubmitArgs>(null, "chat.submit"));
    }

    [Fact]
    public void EventArgsAttribute_rejects_non_record_type()
    {
        Assert.Throws<ArgumentException>(() => new EventArgsAttribute(typeof(string)));
        Assert.Equal(typeof(ClickArgs), new EventArgsAttribute(typeof(ClickArgs)).ArgsType);
    }
}
