using System.Linq;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace CircleRoom;

// A circle's room: who is in it and what they said. The circle publishes join and leave as members accept and go; the room's
// own view emits a message with the field's text.
[State("circleroom")]
public sealed record Room(string[] Members, RoomMessage[] Messages)
{
    public const int MaxMessages = 200;

    public Room() : this([], []) { }
}

public sealed record RoomMessage(string Member, string Text);

public static class CircleRoomUnit
{
    [Synapse("circleroom.join", "circleroom")]
    public static Room Joined(SignalEnvelope signal, Room room)
        => Read(signal.Payload, "member") is { Length: > 0 } member && !room.Members.Contains(member)
            ? room with { Members = [.. room.Members, member] }
            : room;

    [Synapse("circleroom.leave", "circleroom")]
    public static Room Left(SignalEnvelope signal, Room room)
        => Read(signal.Payload, "member") is { Length: > 0 } member
            ? room with { Members = [.. room.Members.Where(known => known != member)] }
            : room;

    [Synapse("circleroom.message", "circleroom")]
    public static Room Said(SignalEnvelope signal, Room room)
    {
        var text = (Read(signal.Payload, "text") ?? Read(signal.Payload, "value"))?.Trim();
        if (string.IsNullOrEmpty(text)) { return room; }
        var member = Read(signal.Payload, "member") is { Length: > 0 } named ? named : "member";
        return room with { Messages = [.. room.Messages.TakeLast(Room.MaxMessages - 1), new RoomMessage(member, text)] };
    }

    [Synapse("circleroom")]
    public static View Show(Room room) => new("Circle room", new Column(
    [
        new Heading("Circle room", Subtitle: room.Members.Length == 0 ? "No one has joined yet." : "Members: " + string.Join(", ", room.Members)),
        new Column([.. room.Messages.Select(message => (Node)new Card(message.Member, message.Text, []))]) { Gap = 8 },
        new Field("message", "circleroom/message", FieldKind.Text, "Message the circle", OnSubmit: new Emit("circleroom", "circleroom.message")),
        new Button("send", "Send", new Emit("circleroom", "circleroom.message")),
    ]) { Gap = 12 });

    // A string member of an object payload, whatever its casing.
    private static string? Read(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) { return null; }
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            { return property.Value.GetString(); }
        }
        return null;
    }
}
