using System.Globalization;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Select;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Viewport;
using DigitalBrain.Flutter.Workspace;

public static class Workspace
{
    public sealed record Command(long Revision, string Action, string Id);
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static IWorkspace State(DigitalBrainConnection brain) => brain.Get<IWorkspace>(Scope(brain));
    public static string Encode(long revision, string action, string id) => JsonSerializer.Serialize(new Command(revision, action, id));

    public static async Task Navigate(DigitalBrainConnection brain, string input, string operation)
    {
        var command = JsonSerializer.Deserialize<Command>(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("A navigation command is required.");
        var current = await State(brain).Read();
        if (command.Revision != current.Revision) { return; }
        var views = current.OpenViews.ToList();
        var pins = current.Pins.ToList();
        var focus = current.FocusedViewId;
        switch (command.Action)
        {
            case "open":
                var app = IApp.Key(Scope(brain), command.Id);
                if (IApp.NameOf(Scope(brain), app) != command.Id || command.Id == "workspace")
                { throw new ArgumentException("Choose an installed app."); }
                var installed = await brain.Get<IApp>(app).Read();
                if (string.IsNullOrEmpty(installed.Manifest.Title)) { throw new ArgumentException("This app is not installed."); }
                if (!views.Any(view => view.Id == command.Id)) { views.Add(new(command.Id, app, "full", "")); }
                focus = command.Id;
                break;
            case "focus":
                var target = views.FirstOrDefault(view => view.Id == command.Id) ?? pins.FirstOrDefault(view => view.Id == command.Id);
                if (target is null) { return; }
                if (!views.Any(view => view.Id == target.Id)) { views.Add(target); }
                focus = target.Id;
                break;
            case "close":
                views.RemoveAll(view => view.Id == command.Id);
                if (focus == command.Id) { focus = views.LastOrDefault()?.Id; }
                break;
            case "pin":
                var pin = views.FirstOrDefault(view => view.Id == command.Id);
                if (pin is not null && !pins.Any(view => view.Id == pin.Id)) { pins.Add(pin); }
                break;
            case "unpin": pins.RemoveAll(view => view.Id == command.Id); break;
            default: throw new ArgumentException("Unknown navigation action.");
        }
        await State(brain).Apply(new([.. views], focus, [.. pins]), operation, current.Revision);
    }

    public static async Task<INeuron[]> Inputs(DigitalBrainConnection brain)
    {
        var key = Key(brain);
        var state = await State(brain).Read();
        List<INeuron> sources = [brain.Get<IButton>(key + "/ino"), brain.Get<IButton>(key + "/pin")];
        for (var i = 0; i < state.OpenViews.Length; i++)
        { sources.Add(brain.Get<IButton>(key + "/focus-" + i)); sources.Add(brain.Get<IButton>(key + "/close-" + i)); }
        for (var i = 0; i < state.Pins.Length; i++) { sources.Add(brain.Get<IButton>(key + "/pinned-" + i)); }
        return [.. sources];
    }

    public static async Task Compose(DigitalBrainConnection brain)
    {
        var key = Key(brain);
        var state = await State(brain).Read();
        var installed = new Dictionary<string, AppSnapshot>(StringComparer.Ordinal);
        await foreach (var instance in brain.Kernel.Instances<IApp>(brain.Stopping))
        {
            if (IApp.NameOf(Scope(brain), instance.Key) is { } name && name != "workspace")
            { installed[name] = await brain.Get<IApp>(instance.Key).Read(); }
        }
        string Title(WorkspaceViewRef view) => installed.GetValueOrDefault(IApp.NameOf(Scope(brain), view.App) ?? "")?.Manifest.Title ?? view.Id;
        await brain.Get<ISelect>(key + "/launcher").Set("Apps", installed.OrderBy(pair => pair.Value.Manifest.Title)
            .Select(pair => new SelectOption(Encode(state.Revision, "open", pair.Key), pair.Value.Manifest.Title)).ToArray(), null);
        var pins = new List<UiChildRef>();
        for (var i = 0; i < state.Pins.Length; i++)
        { pins.Add(await Button(brain, "pinned-" + i, Title(state.Pins[i]), Encode(state.Revision, "focus", state.Pins[i].Id))); }
        var tabs = new List<UiChildRef>();
        for (var i = 0; i < state.OpenViews.Length; i++)
        {
            var view = state.OpenViews[i];
            var focus = await Button(brain, "focus-" + i, (state.FocusedViewId == view.Id ? "· " : "") + Title(view), Encode(state.Revision, "focus", view.Id));
            var close = await Button(brain, "close-" + i, "×", Encode(state.Revision, "close", view.Id));
            tabs.Add(await Layout(brain, "tab-" + i, "row", [focus, close], [0, 32]));
        }
        var focused = state.OpenViews.FirstOrDefault(view => view.Id == state.FocusedViewId);
        var supported = focused is null || string.IsNullOrEmpty(focused.Context);
        var canvas = brain.Get<IViewport>(key + "/canvas");
        await canvas.Set(supported ? focused : null, (await canvas.Read()).Revision);
        await brain.Get<IText>(key + "/empty").Set("Your workspace is clear.\n\nOpen an app from Apps below.");
        await brain.Get<IText>(key + "/unsupported").Set("This view needs context support that is not available yet. You can close it or switch to another app below.");
        await brain.Get<IText>(key + "/usage").Set("Usage unavailable");
        var ino = await Button(brain, "ino", "Ino", Encode(state.Revision, "open", "ino"), installed.ContainsKey("ino"));
        var pinned = focused is not null && state.Pins.Any(view => view.Id == focused.Id);
        var pinButton = await Button(brain, "pin", pinned ? "Unpin" : "Pin", Encode(state.Revision, pinned ? "unpin" : "pin", focused?.Id ?? ""), focused is not null);
        var left = await Layout(brain, "left", "row", [new("select", key + "/launcher"), .. pins], [72, .. pins.Select(_ => 120d)]);
        var center = await Layout(brain, "open-views", "row", [.. tabs], tabs.Select(_ => 180d).ToArray());
        var right = await Layout(brain, "right", "row", [pinButton, ino, new("text", key + "/usage")], [72, 64, 160]);
        var dock = await Layout(brain, "dock", "row", [left, center, right], [Math.Min(360, 80 + 128 * pins.Count), 0, 312]);
        var content = focused is null ? new UiChildRef("text", key + "/empty")
            : !supported ? new UiChildRef("text", key + "/unsupported") : new UiChildRef("viewport", key + "/canvas");
        var root = await Layout(brain, "root", "column", [content, dock], [0, 64]);
        await brain.Get<IApp>(key).Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture), new(Views: [new("full", root)]));
    }

    private static async Task<UiChildRef> Button(DigitalBrainConnection brain, string name, string title, string action, bool enabled = true)
    {
        var key = Key(brain) + "/" + name;
        await brain.Get<IButton>(key).Set(title, action, enabled);
        return new("button", key);
    }

    private static async Task<UiChildRef> Layout(DigitalBrainConnection brain, string name, string mode, UiChildRef[] children, double[]? extents = null)
    {
        var key = Key(brain) + "/" + name;
        var layout = brain.Get<ILayout>(key);
        await layout.Set(new(mode, children, Gap: 8, Extents: extents), (await layout.Read()).Revision);
        return new("layout", key);
    }
}
