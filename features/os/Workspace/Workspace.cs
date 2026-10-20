using System.Globalization;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Compute;
using IntoChat.OS.Flutter.Person;
using IntoChat.OS.Flutter.Layout;
using IntoChat.OS.Flutter.Select;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Workspace;

public static class Workspace
{
    public sealed record Command(long Revision, string Action, string Id);
    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Scope(IntoChatOSConnection brain) => Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static IWorkspace State(IntoChatOSConnection brain) => brain.Get<IWorkspace>(Scope(brain));
    public static string Encode(long revision, string action, string id) => JsonSerializer.Serialize(new Command(revision, action, id));

    public sealed record Activation(string? BrainId, string? Application, string? Neuron, string? Operation, string? Input);

    public static async Task Activate(IntoChatOSConnection brain, string input, string requestId)
    {
        var action = JsonSerializer.Deserialize<Activation>(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("An activation is required.");
        var scope = Scope(brain);
        if (action.BrainId is not null && action.BrainId != scope) { throw new ArgumentException("Connect to the target brain first."); }
        var name = action.Application ?? (action.Neuron is null ? null : IApp.NameOf(scope, action.Neuron));
        if (name is null) { throw new ArgumentException("Choose an installed app."); }
        var state = await State(brain).Read();
        await Navigate(brain, Encode(state.Revision, "open", name), requestId);
        // Only explicit activation carries an operation; ordinary view navigation stays inert.
        if (!string.IsNullOrEmpty(action.Operation))
        { await brain.Get<IApp>(IApp.Key(scope, name)).Request(action.Operation, action.Input ?? ""); }
    }

    public static async Task Navigate(IntoChatOSConnection brain, string input, string operation)
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
                if (installed.MigrationTarget is { } migrated) { app = migrated; }
                var existing = views.FirstOrDefault(view => view.App == app);
                var viewId = existing?.Id ?? IApp.NameOf(Scope(brain), app) ?? command.Id;
                if (existing is null) { views.Add(new(viewId, app, "full", "")); }
                focus = viewId;
                break;
            case "profile":
                await Activate(brain, JsonSerializer.Serialize(new Activation(Scope(brain), "circle", null, "navigate", "{\"page\":\"profile\"}")), operation);
                return;
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

    public static async Task<INeuron[]> Inputs(IntoChatOSConnection brain)
    {
        var key = Key(brain);
        var state = await State(brain).Read();
        List<INeuron> sources = [brain.Get<IButton>(key + "/ino"), brain.Get<IButton>(key + "/usage-button"), brain.Get<IButton>(key + "/profile")];
        for (var i = 0; i < state.OpenViews.Length; i++)
        { sources.Add(brain.Get<IButton>(key + "/focus-" + i)); sources.Add(brain.Get<IButton>(key + "/close-" + i)); }
        for (var i = 0; i < state.Pins.Length; i++) { sources.Add(brain.Get<IButton>(key + "/pinned-" + i)); }
        return [.. sources];
    }

    public static async Task Compose(IntoChatOSConnection brain)
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
        ControlVisual Artwork(string name, bool selected = false, bool running = false, bool compact = true)
        {
            var app = installed.GetValueOrDefault(name);
            var icon = app?.Manifest.Icon;
            var svg = icon is not null && icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? app?.Files.GetValueOrDefault(icon) : null;
            return new(Svg: svg, Icon: svg is null ? "apps" : null, Compact: compact, Selected: selected, Running: running);
        }
        // The launcher lists views, not background services or controls already in the dock.
        var launcher = installed.Where(pair => pair.Key is not ("router" or "selfimprove" or "circleroom" or "ino" or "usage")
            && pair.Value.MigrationTarget is null
            && !string.IsNullOrWhiteSpace(pair.Value.Manifest.Title)
            && (pair.Value.Presentation?.Root?.Main is not null
                || pair.Value.Presentation?.Root?.Views?.Any(view => view.Id == "full") == true
                || pair.Value.Manifest.Operations.Any(operation => operation.Name == "open"))).ToArray();
        var duplicateTitles = launcher.GroupBy(pair => pair.Value.Manifest.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await brain.Get<ISelect>(key + "/launcher").Set("Open an app", launcher.OrderBy(pair => pair.Value.Manifest.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SelectOption(Encode(state.Revision, "open", pair.Key),
                duplicateTitles.Contains(pair.Value.Manifest.Title) ? pair.Value.Manifest.Title + " (" + pair.Key + ")" : pair.Value.Manifest.Title,
                Visual: Artwork(pair.Key, compact: false))).ToArray(), null);
        await brain.Get<ISelect>(key + "/launcher").SetVisual(new(Icon: "apps", Compact: true));
        var pins = new List<UiChildRef>();
        for (var i = 0; i < state.Pins.Length; i++)
        {
            var pin = state.Pins[i];
            if (pin.Id == "ino" || state.OpenViews.Any(view => view.Id == pin.Id)) { continue; }
            pins.Add(await Button(brain, "pinned-" + i, Title(pin), Encode(state.Revision, "focus", pin.Id), visual: Artwork(pin.Id)));
        }
        var tabs = new List<UiChildRef>();
        for (var i = 0; i < state.OpenViews.Length; i++)
        {
            var view = state.OpenViews[i];
            if (view.Id is "ino" or "usage") { continue; }
            tabs.Add(await Button(brain, "focus-" + i, Title(view), Encode(state.Revision, "focus", view.Id),
                visual: Artwork(IApp.NameOf(Scope(brain), view.App) ?? view.Id, state.FocusedViewId == view.Id, true)));
        }
        var focused = state.OpenViews.FirstOrDefault(view => view.Id == state.FocusedViewId);
        // Keep persisted IDs, pins and closed-view receipts; resolve only the presentation target.
        if (focused is not null && installed.GetValueOrDefault(IApp.NameOf(Scope(brain), focused.App) ?? "")?.MigrationTarget is { } replacement)
        { focused = focused with { App = replacement }; }
        var supported = focused is null || string.IsNullOrEmpty(focused.Context);
        var canvas = brain.Get<IViewport>(key + "/canvas");
        await canvas.Set(supported ? focused : null, (await canvas.Read()).Revision);
        await brain.Get<IText>(key + "/empty").Set("Your workspace is clear.\n\nOpen an app from Apps below.");
        await brain.Get<IText>(key + "/unsupported").Set("This view needs context support that is not available yet. You can close it or switch to another app below.");
        var usageLabel = "Usage";
        try
        {
            var usage = await brain.Get<ICompute>(Scope(brain) + "/compute").Read();
            usageLabel = "$" + (usage.SpentMicros / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture);
        }
        catch (Exception) { usageLabel = "Usage"; }
        var usageButton = await Button(brain, "usage-button", usageLabel, Encode(state.Revision, "open", "usage"), installed.ContainsKey("usage"),
            new(Icon: "usage", Selected: state.FocusedViewId == "usage"));
        var profile = await brain.Get<IPerson>(Scope(brain) + "/profile").Read();
        var profileName = string.IsNullOrWhiteSpace(profile.DisplayName) ? "Your profile" : profile.DisplayName;
        var avatar = Uri.TryCreate(profile.AvatarUrl, UriKind.Absolute, out var picture) && picture.Scheme is "http" or "https" ? profile.AvatarUrl : null;
        var profileButton = await Button(brain, "profile", profileName, Encode(state.Revision, "profile", ""), installed.ContainsKey("circle"),
            new(ImageUrl: avatar, Compact: true, Avatar: true));
        var ino = await Button(brain, "ino", "Ino", Encode(state.Revision, "open", "ino"), installed.ContainsKey("ino"),
            Artwork("ino", state.FocusedViewId == "ino", state.OpenViews.Any(view => view.Id == "ino")) with { Tone = "accent" });
        var pinned = focused is not null && state.Pins.Any(view => view.Id == focused.Id);
        List<SelectOption> commands = [];
        if (focused is not null)
        {
            commands.Add(new(Encode(state.Revision, pinned ? "unpin" : "pin", focused.Id), pinned ? "Unpin this app" : "Pin this app"));
            commands.Add(new(Encode(state.Revision, "close", focused.Id), "Close " + Title(focused)));
        }
        if (installed.ContainsKey("settings")) { commands.Add(new(Encode(state.Revision, "open", "settings"), "Settings")); }
        await brain.Get<ISelect>(key + "/more").Set("Workspace options", [.. commands], null);
        await brain.Get<ISelect>(key + "/more").SetVisual(new(Icon: "more", Compact: true));
        var strip = await Layout(brain, "strip", "row", [new("select", key + "/launcher"), ino, .. pins, .. tabs],
            [44, 48, .. pins.Select(_ => 48d), .. tabs.Select(_ => 48d)], gap: 4);
        // New desktop: the shell switches the desktop's layout back and connects again (a select cannot reach the shell).
        // The same shape as Desktop.LayoutActivation (OsDesktopAppFacts checks they match); inlined: apps share no code.
        var desktop = brain.Get<IButton>(key + "/new-desktop");
        var reconnect = JsonSerializer.Serialize(new { kind = "reconnect", brainId = Scope(brain), application = "desktop", operation = "layout", input = "desktop" });
        if ((await desktop.Read()).Activation != reconnect) { await desktop.SetActivation("New desktop", reconnect); }
        if ((await desktop.Read()).Visual is not { Compact: true }) { await desktop.SetVisual(new(Compact: true)); }
        var right = await Layout(brain, "right", "row", [usageButton, profileButton, new("button", key + "/new-desktop"), new("select", key + "/more")], [96, 44, 44, 44], gap: 4);
        var dock = await Layout(brain, "dock", "row", [strip, right], [0, 240], surface: "floating", padding: 8);
        var content = focused is null ? new UiChildRef("text", key + "/empty")
            : !supported ? new UiChildRef("text", key + "/unsupported") : new UiChildRef("viewport", key + "/canvas");
        if (!string.IsNullOrEmpty((await brain.Get<IText>(key + "/navigation-error").Read()).Markdown))
        { content = await Layout(brain, "with-error", "column", [new("text", key + "/navigation-error"), content], [48, 0]); }
        content = await Layout(brain, "content", "column", [content], surface: "panel", padding: 16);
        var root = await Layout(brain, "root", "column", [content, dock], [0, 68], padding: 12);
        await brain.Get<IApp>(key).Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture), new(Views: [new("full", root)]));
    }

    private static async Task<UiChildRef> Button(IntoChatOSConnection brain, string name, string title, string action, bool enabled = true, ControlVisual? visual = null)
    {
        var key = Key(brain) + "/" + name;
        await brain.Get<IButton>(key).Set(title, action, enabled);
        if (visual is not null) { await brain.Get<IButton>(key).SetVisual(visual); }
        return new("button", key);
    }

    private static async Task<UiChildRef> Layout(IntoChatOSConnection brain, string name, string mode, UiChildRef[] children, double[]? extents = null, string? surface = null, double padding = 0, double gap = 8)
    {
        var key = Key(brain) + "/" + name;
        var layout = brain.Get<ILayout>(key);
        await layout.Set(new(mode, children, Gap: gap, Extents: extents, Surface: surface, Padding: padding), (await layout.Read()).Revision);
        return new("layout", key);
    }
}
