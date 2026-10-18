/*
# Workspace
An ordinary C# app composes the canvas and bottom bar. Opening a view never requests
a domain operation; closing never stops the app. Startup restores state without
opening Ino or Discover. Flutter only renders neurons.
open rebuilds the view. navigate accepts JSON {revision, action, id}: open, focus,
close, pin, unpin. Stale inputs cannot move focus. This first local-brain slice has
no foreign grants, external view contexts, device sessions or billing facade.
*/
using IntoChat.OS.Apps;
static class App
{
    public const string Name = "workspace";
    public const string Version = "0.3.5";
    public static readonly AppManifest Manifest = new(
        Title: "Workspace", Description: "Your apps, in one quiet workspace.",
        Operations: [new("open", "Rebuild without changing focus."), new("navigate", "Apply a revision-bound navigation command."), new("activate", "Open an app and dispatch an explicit user action.")],
        Settings: [], Slots: []);
}
