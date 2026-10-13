/*
# FilmCraft

FilmCraft is an open-source, Premiere-compatible video editor (timeline, trims, Lumetri color, audio, export).

## Open

The Connect editor toolbar action connects the brain to FilmCraft's MCP server and prepares its web app in the main layout. The
page's own agent API (`window.filmcraft`) becomes the tool source `pages/filmcraft`, so the assistant and
the person edit one timeline. The page answers only while its browser is mounted.

## Scenario: Connecting prepares the tools and editor

Invoking "open" connects the brain's `mcp/filmcraft` server (its tools, such as `command_list`, appear
in search), points the main browser at the FilmCraft page, configures `pages/filmcraft` with that
browser as its driver and sets the status to "FilmCraft tools connected." in the stable main layout.

## Scenario: Connecting again keeps one connection

Invoking "open" a second time keeps the same main layout, the server connected and the status
"FilmCraft tools connected.".

## Scenario: Cut by asking

With FilmCraft focused, asking "list the timeline commands" runs the page tool `commands` with filter
`timeline` and answers with the command ids.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "filmcraft";
    public const string Version = "1.0.6";

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
