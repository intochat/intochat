namespace DigitalBrain.Apps;

internal sealed record AppConnectionOptions(string Revision, IReadOnlyList<AppConnectionSlot> Slots);
internal sealed record AppConnectionSlot(string Name, string Source, string Description, IReadOnlyList<AppConnectionChoice> Accounts);
internal sealed record AppConnectionChoice(string Id, string Label);
