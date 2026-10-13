using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;

// Delivery is at-least-once, and a fresh subscription is seeded with the requests the app retains, so a
// forwarding synapse would otherwise request another conversation or begin a finished voice capture.
// Each forwarding synapse remembers the Ino request id before it can be replayed. Each request
// synapse keeps the ids it has handled in a ledger text of its own and skips a replayed id. The ledger
// is the synapse's state, so a reaped script reads it back on startup (one read).
public sealed class RequestLedger
{
    private const int Kept = 256;
    private readonly IText _text;
    private readonly List<Guid> _ids;

    private RequestLedger(IText text, List<Guid> ids) { _text = text; _ids = ids; }

    public static async Task<RequestLedger> LoadAsync(DigitalBrainConnection brain, string synapse)
    {
        var text = brain.Get<IText>(brain.Setting("App")! + "/handled/" + synapse);
        var ids = (await text.Read()).Markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Guid.TryParse(line, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
        return new(text, ids);
    }

    public bool Handled(Guid id) => _ids.Contains(id);

    public Task Record(Guid id)
    {
        _ids.Add(id);
        if (_ids.Count > Kept) { _ids.RemoveRange(0, _ids.Count - Kept); }
        return _text.Set(string.Join('\n', _ids));
    }
}
