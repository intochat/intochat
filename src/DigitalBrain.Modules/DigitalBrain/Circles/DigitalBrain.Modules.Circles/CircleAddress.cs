using DigitalBrain.Framework;

namespace DigitalBrain.Circles;

// What a circle neuron key says: the brain that holds the copy, the circle's name and its owner.
internal readonly record struct CircleAddress(string Brain, string Name, string Owner)
{
    private const string Marker = "/circles/";
    private const string ChatSuffix = "/chat";

    public bool IsOwnerCopy => Brain == Owner;
    public string CircleKey => ICircle.Key(Brain, Name, Owner);
    public string OwnerKey => ICircle.Key(Owner, Name);
    public string ChatKey => ICircleChat.Key(CircleKey);

    public static CircleAddress Of(string circleKey)
        => TryParse(circleKey) ?? throw new ArgumentException($"'{circleKey}' is not a circle key.", nameof(circleKey));

    public static CircleAddress OfChat(string chatKey)
        => chatKey.EndsWith(ChatSuffix, StringComparison.Ordinal) ? Of(chatKey[..^ChatSuffix.Length])
            : throw new ArgumentException($"'{chatKey}' is not a circle chat key.", nameof(chatKey));

    public static CircleAddress? TryParse(string key)
    {
        var at = key.IndexOf(Marker, StringComparison.Ordinal);
        if (at <= 0) { return null; }
        var brain = key[..at];
        var rest = key[(at + Marker.Length)..];
        var owner = brain;
        var marker = rest.IndexOf('@');
        if (marker >= 0) { owner = rest[(marker + 1)..]; rest = rest[..marker]; }
        if (!BrainScope.IsValidId(brain) || !BrainScope.IsValidId(owner) || !ICircle.IsValidName(rest) || marker >= 0 && owner == brain) { return null; }
        return new(brain, rest, owner);
    }
}
