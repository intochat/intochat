using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;

// A fresh subscription receives retained app requests. Remember completed Up request ids, including
// no-ops, so replay cannot navigate again from a later location. The ledger survives script restart.
public sealed class RequestLedger
{
    private const int Kept = 256;
    private readonly IText _text;
    private readonly List<Guid> _ids;

    private RequestLedger(IText text, List<Guid> ids) { _text = text; _ids = ids; }

    public static async Task<RequestLedger> LoadAsync(IntoChatOSConnection brain, string synapse)
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
