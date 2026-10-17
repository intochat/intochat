namespace DigitalBrain.Apps.Signals;

// One shipped copy brought to the catalog's version: the app's name and the versions before and
// after. Published by the reconciler so the shell and traces show every catch-up.
[GenerateSerializer, Alias("apps.app-reconciled")]
public sealed record AppReconciled(
    [property: Id(0)] string Name,
    [property: Id(1)] string From,
    [property: Id(2)] string To) : Signal;
