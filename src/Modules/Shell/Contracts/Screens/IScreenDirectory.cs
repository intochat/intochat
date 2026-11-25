using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Flutter.Screens;

/// <summary>
/// Unified screen gateway for listing, reading and acting on UI across built-in apps and runtime units. It answers no confirmation:
/// a person answers one on POST /brains/{id}/confirmations/{planId}.
/// </summary>
[NeuronAlias("ui.screens"), DefaultNeuronType("ui.screens")]
public interface IScreenDirectory
{
    const string TypeName = "ui.screens";

    static string Key(string brainId) => $"{brainId}/main";

    [ReadOnlyCall]
    Task<ScreenSummary[]> List();

    [ReadOnlyCall]
    Task<ScreenSnapshot?> Read(string appId, string instanceId = "main");

    Task<ScreenActionResult> Act(string appId, string key, string? value = null, long? generation = null, string instanceId = "main");
}

[GenerateSerializer, Alias("ui.screen-summary")]
public sealed record ScreenSummary(
    [property: Id(0)] string AppId,
    [property: Id(1)] string Title,
    [property: Id(2)] string Kind,
    [property: Id(3)] bool OpenInWorkspace,
    [property: Id(4)] bool Focused,
    [property: Id(5)] int PendingApprovals,
    [property: Id(6)] string InstanceId = "main");

// A keyed node of a screen's view that a person can act on. The alias is the record's wire identity from before the rename and stays.
[GenerateSerializer, Alias("ui.screen-control")]
public sealed record ScreenNodeItem(
    [property: Id(0)] string Key,
    [property: Id(1)] string Kind,
    [property: Id(2)] string Label,
    [property: Id(3)] string? Value = null);

[GenerateSerializer, Alias("ui.screen-approval")]
public sealed record ScreenApprovalItem(
    [property: Id(0)] string ApprovalId,
    [property: Id(1)] string Title,
    [property: Id(2)] string Summary,
    [property: Id(3)] string AppId,
    [property: Id(4)] string Status);

[GenerateSerializer, Alias("ui.screen-snapshot")]
public sealed record ScreenSnapshot(
    [property: Id(0)] string AppId,
    [property: Id(1)] string InstanceId,
    [property: Id(2)] string Title,
    [property: Id(3)] string Kind,
    [property: Id(4)] long Generation,
    [property: Id(5)] View? View,
    [property: Id(6)] ScreenNodeItem[] Controls,
    [property: Id(7)] ScreenApprovalItem[] PendingApprovals);

[GenerateSerializer, Alias("ui.screen-action-result")]
public sealed record ScreenActionResult(
    [property: Id(0)] bool Accepted,
    [property: Id(1)] string Message,
    [property: Id(2)] ScreenSnapshot? Screen = null);
