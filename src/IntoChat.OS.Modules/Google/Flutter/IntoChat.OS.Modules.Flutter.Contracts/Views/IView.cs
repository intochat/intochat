using Orleans.Concurrency;

namespace IntoChat.OS.Flutter.Views;

[Alias("view"), Orleans.Metadata.DefaultGrainType(UIVocabulary.ViewType)]
public interface IView : INeuron
{
    Task Publish(UiView view);
    [ReadOnly] Task<UiView?> Read();
    Task Activate(long generation, string key, string expectedAction, Guid requestId);
}

[GenerateSerializer, Alias("ui.view")]
public sealed record UiView([property: Id(0)] long Generation, [property: Id(1)] long Version, [property: Id(2)] UiNode Root);

public enum UiNodeKind { Text = 0, Button = 1, Column = 2, Row = 3, Card = 4, Reference = 5, Menu = 6, Modal = 7, Toast = 8, List = 9, Badge = 10, Avatar = 11, Divider = 12, Tabs = 13 }

public enum UiStyle { Primary = 0, Secondary = 1, Ghost = 2, Danger = 3, Link = 4 }

public enum UiTone { Neutral = 0, Done = 1, Working = 2, Error = 3 }

[GenerateSerializer, Alias("ui.node")]
public sealed record UiNode([property: Id(0)] UiNodeKind Kind, [property: Id(1)] string Key)
{
    [Id(2)] public string? Text { get; init; }
    [Id(3)] public string? Detail { get; init; }
    [Id(4)] public string? Action { get; init; }
    [Id(5)] public bool Enabled { get; init; } = true;
    [Id(6)] public UiNode[] Children { get; init; } = [];
    [Id(7)] public double[]? Extents { get; init; }
    [Id(8)] public UiChildRef? Reference { get; init; }
    [Id(9)] public double Gap { get; init; } = 12;
    [Id(10)] public bool FollowEnd { get; init; }
    [Id(11)] public string? Icon { get; init; }
    [Id(12)] public UiStyle Style { get; init; }
    [Id(13)] public UiTone Tone { get; init; }
}

public static class Ui
{
    public static UiNode Text(string key, string text) => new(UiNodeKind.Text, key) { Text = text };
    public static UiNode Button(string key, string text, string action, bool enabled = true, UiStyle style = UiStyle.Secondary, string? icon = null) => new(UiNodeKind.Button, key) { Text = text, Action = action, Enabled = enabled, Style = style, Icon = icon };
    // Text is the accessible label; the shell shows only the icon.
    public static UiNode IconButton(string key, string icon, string label, string action) => new(UiNodeKind.Button, key) { Text = label, Action = action, Style = UiStyle.Ghost, Icon = icon };
    public static UiNode Column(string key, UiNode[] children, double gap = 12, double[]? extents = null, bool followEnd = false) => new(UiNodeKind.Column, key) { Children = children, Gap = gap, Extents = extents, FollowEnd = followEnd };
    public static UiNode Row(string key, UiNode[] children, double gap = 12, double[]? extents = null) => new(UiNodeKind.Row, key) { Children = children, Gap = gap, Extents = extents };
    public static UiNode Card(string key, string text, string? detail = null, UiNode[]? children = null) => new(UiNodeKind.Card, key) { Text = text, Detail = detail, Children = children ?? [] };
    public static UiNode Reference(string key, UiChildRef reference) => new(UiNodeKind.Reference, key) { Reference = reference };
    public static UiNode Menu(string key, string label, UiNode[] items, string? icon = null) => new(UiNodeKind.Menu, key) { Text = label, Children = items, Icon = icon };
    public static UiNode Modal(string key, string title, string? detail, UiNode[] children) => new(UiNodeKind.Modal, key) { Text = title, Detail = detail, Children = children };
    public static UiNode Toast(string key, string text, UiTone tone = UiTone.Neutral, UiNode? action = null) => new(UiNodeKind.Toast, key) { Text = text, Tone = tone, Children = action is null ? [] : [action] };
    public static UiNode List(string key, UiNode[] rows) => new(UiNodeKind.List, key) { Children = rows };
    public static UiNode Badge(string key, string text, UiTone tone) => new(UiNodeKind.Badge, key) { Text = text, Tone = tone };
    public static UiNode Avatar(string key, string initials) => new(UiNodeKind.Avatar, key) { Text = initials };
    public static UiNode Divider(string key, string? label = null) => new(UiNodeKind.Divider, key) { Text = label };
    public static UiNode Tabs(string key, UiNode[] tabs) => new(UiNodeKind.Tabs, key) { Children = tabs };
    public static UiNode Tab(string key, string label, string action, bool active) => new(UiNodeKind.Button, key) { Text = label, Action = action, Style = active ? UiStyle.Primary : UiStyle.Ghost };
}

[GenerateSerializer, Alias("ui.view-changed")]
public sealed record ViewChanged([property: Id(0)] string Name, [property: Id(1)] long Generation, [property: Id(2)] long Version) : Signal;

[GenerateSerializer, Alias("ui.view-action")]
public sealed record ViewAction([property: Id(0)] string Name, [property: Id(1)] string Key, [property: Id(2)] string Action, [property: Id(3)] Guid RequestId, [property: Id(4)] long Generation) : Signal;
