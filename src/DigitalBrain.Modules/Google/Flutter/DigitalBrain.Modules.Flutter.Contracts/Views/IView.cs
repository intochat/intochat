using Orleans.Concurrency;

namespace DigitalBrain.Flutter.Views;

[Alias("view"), Orleans.Metadata.DefaultGrainType(UIVocabulary.ViewType)]
public interface IView : INeuron
{
    Task Publish(UiView view);
    [ReadOnly] Task<UiView?> Read();
    Task Activate(long generation, string key, string expectedAction, Guid requestId);
}

[GenerateSerializer, Alias("ui.view")]
public sealed record UiView([property: Id(0)] long Generation, [property: Id(1)] long Version, [property: Id(2)] UiNode Root);

public enum UiNodeKind { Text = 0, Button = 1, Column = 2, Row = 3, Card = 4, Reference = 5 }

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
}

public static class Ui
{
    public static UiNode Text(string key, string text) => new(UiNodeKind.Text, key) { Text = text };
    public static UiNode Button(string key, string text, string action, bool enabled = true) => new(UiNodeKind.Button, key) { Text = text, Action = action, Enabled = enabled };
    public static UiNode Column(string key, UiNode[] children, double gap = 12, double[]? extents = null, bool followEnd = false) => new(UiNodeKind.Column, key) { Children = children, Gap = gap, Extents = extents, FollowEnd = followEnd };
    public static UiNode Row(string key, UiNode[] children, double gap = 12, double[]? extents = null) => new(UiNodeKind.Row, key) { Children = children, Gap = gap, Extents = extents };
    public static UiNode Card(string key, string text, string? detail = null, UiNode[]? children = null) => new(UiNodeKind.Card, key) { Text = text, Detail = detail, Children = children ?? [] };
    public static UiNode Reference(string key, UiChildRef reference) => new(UiNodeKind.Reference, key) { Reference = reference };
}

[GenerateSerializer, Alias("ui.view-changed")]
public sealed record ViewChanged([property: Id(0)] string Name, [property: Id(1)] long Generation, [property: Id(2)] long Version) : Signal;

[GenerateSerializer, Alias("ui.view-action")]
public sealed record ViewAction([property: Id(0)] string Name, [property: Id(1)] string Key, [property: Id(2)] string Action, [property: Id(3)] Guid RequestId, [property: Id(4)] long Generation) : Signal;
