/*
# FilmCraft

FilmCraft is an open-source, Premiere-compatible video editor (timeline, trims, Lumetri color, audio, export).

## Open

The Connect editor toolbar action connects the brain to FilmCraft's MCP server and prepares its web app in the declared surface. The
page's own agent API (`window.filmcraft`) becomes the tool source `pages/filmcraft`, so the assistant and
the person edit one timeline. The page answers only while its window is open.

## Scenario: Connecting prepares the tools and editor

Invoking "open" connects the brain's `mcp/filmcraft` server (its tools, such as `command_list`, appear
in search), points the window's browser at the FilmCraft page, configures `pages/filmcraft` with that
browser as its driver and sets the status to "FilmCraft tools connected." in the stable surface.

## Scenario: Connecting again keeps one connection

Invoking "open" a second time keeps the same surface, the server connected and the status
"FilmCraft tools connected.".

## Scenario: Cut by asking

With the window open, asking "list the timeline commands" runs the page tool `commands` with filter
`timeline` and answers with the command ids.
Workspace hosting: startup composes the declared entry using stable neuron identities. Opening the
workspace reads that state; composition and completion never change focus or reopen a closed entry.

*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "filmcraft";
    public const string Version = "1.0.5";

    public static readonly AppManifest Manifest = new(
        Title: "FilmCraft",
        Description: "Premiere-style video editing: the app runs in a window and the assistant edits the same timeline through its page API.",
        Operations:
        [
            new("open", "Connect FilmCraft's tools to the brain and prepare the live editor."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg",
        Toolbar: [new("open", Icon: "link", Label: "Connect editor")]);
}
