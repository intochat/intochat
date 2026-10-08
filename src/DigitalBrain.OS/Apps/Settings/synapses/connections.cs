using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Integrations;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var scope = brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
var app = brain.Get<IApp>(appKey);
var connections = brain.Get<IConnectionRequests>(scope);
var layout = brain.Get<ILayout>(appKey + "/connections");

using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
var loops = new[] { Operations(), RefreshClicks() };
await Task.WhenAny(loops);
lifetime.Cancel();
await Task.WhenAll(loops);

async Task Operations()
{
    await foreach (var _ in brain.On<AppInvoked>(app, Reconcile, lifetime.Token)) { await Reconcile(); }
}

async Task RefreshClicks()
{
    await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(appKey + "/refresh-connections"), lifetime.Token))
    { await app.Invoke(new(clicked.RequestId == Guid.Empty ? Guid.NewGuid() : clicked.RequestId, "refresh-connections", "")); }
}

async Task Reconcile()
{
    var requests = (await app.Pending()).Where(item => item.Operation == "refresh-connections").ToArray();
    if (requests.Length == 0) { return; }
    string? error = null;
    object? result = null;
    try { result = await Refresh(); }
    catch (Exception) when (!lifetime.Token.IsCancellationRequested)
    {
        error = "Connections could not load. Use Refresh connections to try again.";
        await Show([await Card("error", "Connections unavailable", error)]);
    }
    foreach (var request in requests)
    { await app.Respond(new(request.Id, error is null ? JsonSerializer.Serialize(result) : null, error)); }
}

async Task<object> Refresh()
{
    await Show([await Card("loading", "Loading connections", "Reading this brain's connections and app bindings…")]);
    var kinds = await connections.ListKinds();
    var current = await connections.ListConnections();
    var pending = await connections.ListPending();
    var apps = new List<(string Name, AppSnapshot State)>();
    foreach (var name in await brain.Get<IAppIndex>(scope).List())
    { apps.Add((name, await brain.Get<IApp>(IApp.Key(scope, name)).Read())); }
    string Provider(string id) => kinds.FirstOrDefault(kind => kind.Id == id)?.DisplayName ?? id;
    var cards = new List<UiChildRef>
    {
        await Card("intro", "Your connections", "Connections belong to this brain. Available connector types are workspace capabilities, not connected accounts. Deployment integrations are managed separately.")
    };
    if (current.Length == 0)
    { cards.Add(await Card("empty", "No connections yet", "Use the workspace connection authorization controls to connect a service, then refresh this view.")); }
    foreach (var connection in current.OrderBy(item => item.Kind).ThenBy(item => item.Id))
    {
        var users = apps.SelectMany(item => item.State.Manifest.Slots
            .Where(slot => slot.Connector == connection.Kind && item.State.Connections.GetValueOrDefault(slot.Name) == connection.Id)
            .Select(slot => item.State.Manifest.Title + " (" + slot.Name + ")")).ToArray();
        var body = State(connection.Status) + "\n" +
            (connection.LastProbedAt is { } probe ? "Last checked: " + probe.ToString("u") : "No credential check recorded.") + "\n" +
            (users.Length == 0 ? "No saved app bindings." : "Used by: " + string.Join(", ", users));
        cards.Add(await Card("connection/" + Key(connection.Kind + "/" + connection.Id), Provider(connection.Kind) + " · " + connection.Id, body));
    }
    cards.Add(await Card("apps-title", "App connections", "Saved bindings are shown below. Apps without saved bindings may choose a connection during a request."));
    foreach (var item in apps.OrderBy(item => item.State.Manifest.Title))
    {
        var bindings = item.State.Manifest.Slots.Select(slot =>
        {
            var id = item.State.Connections.GetValueOrDefault(slot.Name);
            var found = current.FirstOrDefault(connection => connection.Kind == slot.Connector && connection.Id == id);
            return slot.Name + " · " + Provider(slot.Connector) + " · " +
                (string.IsNullOrWhiteSpace(id) ? "Configuration required" : id + " · " + (found is null ? "Unavailable" : State(found.Status)));
        }).ToArray();
        cards.Add(await Card("app/" + Key(item.Name), item.State.Manifest.Title,
            bindings.Length == 0 ? "No saved connection bindings." : string.Join("\n", bindings)));
    }
    cards.Add(await Card("available-title", "Available connector types", "These services can be configured in this workspace. Availability does not grant access to an account."));
    foreach (var kind in kinds.OrderBy(item => item.DisplayName))
    {
        var count = current.Count(item => item.Kind == kind.Id);
        var waiting = pending.Count(item => item.Kind == kind.Id);
        cards.Add(await Card("kind/" + Key(kind.Id), kind.DisplayName,
            (count == 0 ? "Not connected" : count + " configured connection(s)") +
            (waiting == 0 ? "" : " · " + waiting + " authorization request(s) pending")));
    }
    await Show(cards);
    return new { connections = current, available = kinds, apps = apps.Select(item => new { name = item.Name, title = item.State.Manifest.Title, bindings = item.State.Connections }).ToArray() };
}

async Task<UiChildRef> Card(string key, string title, string body)
{
    var name = appKey + "/connections/" + key;
    await brain.Get<ICard>(name).Set(title, body);
    return new(UIVocabulary.CardType, name);
}

async Task Show(IReadOnlyList<UiChildRef> children)
{ await layout.Set(new("list", children.ToArray()), (await layout.Read()).Revision); }

static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
static string State(string value) => value switch
{
    "connected" => "Connected", "expired" => "Authorization expired", "failing" => "Unavailable — check the connection",
    "disconnected" => "Not connected", _ => "Configuration required"
};
