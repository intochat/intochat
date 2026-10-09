# PhotoCraft

PhotoCraft is an open-source, Photoshop-compatible image editor (layers, masks, adjustment layers, PSD).

## Open

Opening connects the brain to PhotoCraft's MCP server, so its tools (documents, `command_list`,
`command_run`, `render_preview`, ...) appear in the assistant's search, and shows the PhotoCraft web app
in a window.

## Scenario: Edit by asking

Asking the assistant "make a 512x512 image with a blue-to-orange gradient" finds PhotoCraft's tools,
creates a document, runs `layer.newFillLayer.gradient` and answers with a rendered preview.
