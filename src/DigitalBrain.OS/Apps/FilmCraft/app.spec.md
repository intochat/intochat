# FilmCraft

FilmCraft is an open-source, Premiere-compatible video editor (timeline, trims, Lumetri color, audio, export).

## Open

Opening connects the brain to FilmCraft's MCP server and shows the FilmCraft web app in a window. The
page's own agent API (`window.filmcraft`) becomes the tool source `pages/filmcraft`, so the assistant and
the person edit one timeline. The page answers only while its window is open.

## Scenario: Cut by asking

With the window open, asking "list the timeline commands" runs the page tool `commands` with filter
`timeline` and answers with the command ids.
