namespace IntoChat.OS.Modules.Apps.Signals;

// One shipped copy brought to the catalog's version: the app's name and the versions before and
// after. Published by the reconciler so the shell and traces show every catch-up.
[DigitalBrain.Signal("apps.app-reconciled")]
public sealed record AppReconciled(
    string Name,
    string From,
    string To) : DigitalBrain.Signal;
