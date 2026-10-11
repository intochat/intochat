/*
# FilmCraft

FilmCraft is an open-source, Premiere-compatible video editor (timeline, trims, Lumetri color, audio, export).

## Open

Opening connects the brain to FilmCraft's MCP server and shows the FilmCraft web app in a window. The
page's own agent API (`window.filmcraft`) becomes the tool source `pages/filmcraft`, so the assistant and
the person edit one timeline. The page answers only while its window is open.

## Scenario: Opening connects the tools and shows the window

Invoking "open" connects the brain's `mcp/filmcraft` server (its tools, such as `command_list`, appear
in search), points the window's browser at the FilmCraft page, configures `pages/filmcraft` with that
browser as its driver, sets the status to "FilmCraft tools connected." and opens a "FilmCraft" window
whose surface holds the status and the browser.

## Scenario: Opening again keeps one window

Invoking "open" a second time leaves one "FilmCraft" window, the server still connected and the status
"FilmCraft tools connected.".

## Scenario: Cut by asking

With the window open, asking "list the timeline commands" runs the page tool `commands` with filter
`timeline` and answers with the command ids.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "filmcraft";
    public const string Version = "1.0.3";

    public static readonly AppManifest Manifest = new(
        Title: "FilmCraft",
        Description: "Premiere-style video editing: the app runs in a window and the assistant edits the same timeline through its page API.",
        Operations:
        [
            new("open", "Connect FilmCraft's tools to the brain and open its window with the live app."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
