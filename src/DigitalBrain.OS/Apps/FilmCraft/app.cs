/*
# FilmCraft

FilmCraft is an open-source, Premiere-compatible video editor (timeline, trims, Lumetri color, audio, export).

## Open

Opening connects the brain to FilmCraft's MCP server and shows the FilmCraft web app in a window. The
page's own agent API (`window.filmcraft`) becomes the tool source `pages/filmcraft`, so the assistant and
the person edit one timeline. The page answers only while its window is open.

## Scenario: Cut by asking

With the window open, asking "list the timeline commands" runs the page tool `commands` with filter
`timeline` and answers with the command ids.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "filmcraft";
    public const string Version = "1.0.2";

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
