using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.UiKit;

/// <summary>Opens the UI Kit on a page (the first scenario when empty or unknown).</summary>
/// <param name="Page">The page id: a scenario id or a catalog kind.</param>
[Signal("uikit.open")]
public sealed record UiKitOpen(string? Page = null) : Signal;

/// <summary>Appends the next sample point to the live chart the current page shows.</summary>
[Signal("uikit.append")]
public sealed record UiKitAppend : Signal;

/// <summary>A command a data node example sent: the UI Kit logs it instead of refusing its made-up signal.</summary>
/// <param name="Kind">The kind of the example that sent it.</param>
/// <param name="OriginalSignal">The signal alias the example's command named.</param>
/// <param name="ArgsJson">The example command's args as JSON ("null" when none).</param>
[Signal(UiKitPages.ShowcaseCommandSignal)]
public sealed record UiKitShowcaseCommand(string? Kind = null, string? OriginalSignal = null, string? ArgsJson = null) : Signal;

/// <summary>A signal a slotted control published, as the event log shows it.</summary>
/// <param name="Control">The slot key of the control that published it.</param>
/// <param name="Signal">The signal alias.</param>
/// <param name="ArgsJson">The signal's arguments as wire JSON.</param>
/// <param name="At">When the UI Kit received it.</param>
public sealed record UiKitEvent(string Control, string Signal, string ArgsJson, DateTimeOffset At);

/// <summary>The UI Kit's state: the page shown, the controlled switch's value, the model the composer page picked, the kanban page's cards, and the event log.</summary>
public sealed record UiKitState
{
    /// <summary>The most events the log keeps; older ones are dropped.</summary>
    public const int MaxLog = 50;

    /// <summary>The page shown: a scenario id or a catalog kind.</summary>
    public string Page { get; init; } = UiKitPages.First;

    /// <summary>The value of the controlled switch demo: the app owns it.</summary>
    public bool ControlledOn { get; init; }

    /// <summary>The model id the composer page's picker last selected.</summary>
    public string Model { get; init; } = UiKitPages.Models[0].Id;

    /// <summary>How many points the Append button has added (the next point's number).</summary>
    public int Appended { get; init; }

    /// <summary>The kanban page's cards, in column order: the app owns them, and a move the board signals moves one here.</summary>
    public KanbanCardItem[] Cards { get; init; } = [.. UiKitPages.Cards];

    /// <summary>The last <see cref="MaxLog"/> signals the slotted controls published, oldest first.</summary>
    public UiKitEvent[] Log { get; init; } = [];

    [Invariant("The event log keeps at most 50 entries."), JsonIgnore]
    public bool LogBounded => Log.Length <= MaxLog;

    /// <summary>This state with <paramref name="entry"/> logged (the oldest dropped past <see cref="MaxLog"/>).</summary>
    public UiKitState Logged(UiKitEvent entry) => this with { Log = [.. Log.Append(entry).TakeLast(MaxLog)] };

    /// <inheritdoc />
    public bool Equals(UiKitState? other) => other is not null && Page == other.Page && ControlledOn == other.ControlledOn && Model == other.Model
        && Appended == other.Appended && Log.SequenceEqual(other.Log) && Cards.SequenceEqual(other.Cards);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Page, ControlledOn, Model, Appended, Log.Length);
}
