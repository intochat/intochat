using DigitalBrain.Flutter;

namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.operation2")]
public sealed record AppOperation([property: Id(0)] string Name, [property: Id(1)] string Description);

[GenerateSerializer, Alias("apps.setting2")]
public sealed record AppSetting([property: Id(0)] string Name, [property: Id(1)] string Description, [property: Id(2)] string Default);

// A named credential slot ("gmail"); the bound connection id is per-brain state, never shared.
[GenerateSerializer, Alias("apps.slot")]
public sealed record AppSlot([property: Id(0)] string Name, [property: Id(1)] string Connector);

public static class AppToolbarKinds
{
    public const string Button = "button";
    public const string Toggle = "toggle";
    public const string Slider = "slider";
    public const string Text = "text";
    public static readonly string[] All = [Button, Toggle, Slider, Text];
}

// One control in the dock: how an operation the app declares is shown, and what input it posts
// (a button "", a toggle "on"/"off", a slider its value as text on release, a text field its text on submit).
[GenerateSerializer, Alias("apps.toolbar-entry")]
public sealed record AppToolbarEntry(
    [property: Id(0)] string Operation,
    [property: Id(1)] string Kind = AppToolbarKinds.Button,
    [property: Id(2)] string? Icon = null,
    [property: Id(3)] string? Label = null,
    [property: Id(4)] double? Min = null,
    [property: Id(5)] double? Max = null);

[GenerateSerializer, Alias("apps.manifest2")]
public sealed record AppManifest(
    [property: Id(0)] string Title,
    [property: Id(1)] string Description,
    [property: Id(2)] AppOperation[] Operations,
    [property: Id(3)] AppSetting[] Settings,
    [property: Id(4)] AppSlot[] Slots,
    [property: Id(5)] string? Icon = null,
    [property: Id(6)] AppToolbarEntry[]? Toolbar = null,
    [property: Id(7)] UiChildRef? Entry = null)
{
    // The dock renders only what the app can answer: every entry names a declared operation with one of the four kinds.
    public AppManifest Validated()
    {
        if (Entry is { } root)
        {
            if (root.Kind is not ("surface" or "layout" or "collection" or "imagecanvas" or "card" or "button" or "textfield" or "tabs" or "text" or "table" or "chart" or "scene" or "chat" or "composer" or "select" or "fileinput" or "voiceinput" or "webbrowser" or "form"))
            { throw new InvalidDataException($"Unsupported app entry kind '{root.Kind}'."); }
            if (string.IsNullOrWhiteSpace(root.Name) || root.Name.Length > 256
                || root.Name.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."
                    || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            { throw new InvalidDataException("An app entry needs a safe installation-relative neuron name."); }
        }
        foreach (var entry in Toolbar ?? [])
        {
            if (!Operations.Any(operation => operation.Name == entry.Operation))
            {
                throw new InvalidDataException($"Toolbar entry '{entry.Operation}' is not an operation of this app. It offers: "
                    + string.Join(", ", Operations.Select(operation => operation.Name)) + ".");
            }
            if (!AppToolbarKinds.All.Contains(entry.Kind, StringComparer.Ordinal))
            { throw new InvalidDataException($"Toolbar entry '{entry.Operation}' has kind '{entry.Kind}'; a kind is button, toggle, slider or text."); }
            if (entry.Kind == AppToolbarKinds.Slider && !(entry.Min < entry.Max))
            { throw new InvalidDataException($"Toolbar slider '{entry.Operation}' needs min below max."); }
        }
        return this;
    }
}
