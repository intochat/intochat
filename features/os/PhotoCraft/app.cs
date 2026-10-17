/*
# PhotoCraft

PhotoCraft is an open-source, Photoshop-compatible image editor (layers, masks, adjustment layers, PSD).

## Open

Startup connects the brain to PhotoCraft's MCP server, so its tools (documents, `command_list`,
`command_run`, `render_preview`, ...) appear in the assistant's search, and prepares the PhotoCraft web app
in the main layout: a status line above the app's page. The Connect editor action retries a failed
connection. Startup creates no image and starts no editor turn.

## Edit

"edit" hands a request in plain words to PhotoCraft's editor: an agent turn that may act only through
PhotoCraft's own tools, never on files or the PC. The status line says what is being edited; when the
turn ends, the editor's answer replaces it and the active document is rendered into the preview image
(`preview`, a PNG data URL captioned with the request). A failed turn says so on the status line. One edit
runs at a time; a request redelivered later starts nothing new.

"edit" is how the assistant makes images: the router's app tier can choose it for an image request, and
the app's description points the assistant at it rather than at PhotoCraft's raw tools. The operation's
description tells the assistant that the request returns at once, which neurons hold the answer, and that
its own turn need not wait for the editor: it checks the status once ("Editing: ..." means the request was
taken, "still working" means it was not) and answers.

## Preview

The preview always shows the active document. When PhotoCraft's tools change the document outside an
edit (the assistant ran them itself), the document is rendered into the preview again, captioned "Changed
with PhotoCraft's tools.". Tools that only read or render change nothing, an image that did not change is not
written again, and during an edit the preview waits for the editor's turn to end.

## Scenario: Connecting prepares the tools and editor

Invoking "open" connects the brain's PhotoCraft MCP server (its tools, such as `command_run`, appear in
the brain's search), sets the status to "PhotoCraft tools connected.", points the browser at the
PhotoCraft web app in the main layout with the status and browser.

## Scenario: Connecting again keeps one connection and stable main layout

Invoking "open" a second time leaves the MCP server connected with the same tools and the workspace
with the same PhotoCraft presentation.

## Scenario: Edit by asking

Invoking "edit" with "make a 512x512 image with a blue-to-orange gradient" creates a 512x512 document,
runs `layer.newFillLayer.gradient`, sets the status to the editor's answer and the preview to a
rendered PNG of the blue-to-orange gradient.

## Scenario: An empty edit is refused

Invoking "edit" with only spaces starts no editor turn and the status says what to send.

## Scenario: One edit at a time

Invoking "edit" while the editor is still working on another edit starts no second turn; the status says
PhotoCraft is still working, and the first edit's answer replaces it when that edit finishes.

## Scenario: A cancelled edit says so

Cancelling the editor's running turn sets the status to "The edit did not finish: " and the reason.

## Scenario: Changes made with PhotoCraft's tools reach the preview

Running PhotoCraft's `doc_new` and then `command_run` with `layer.newFillLayer.gradient` directly, outside
an edit, renders the new gradient document into the preview, captioned "Changed with PhotoCraft's tools.".
Running `doc_inspect` afterwards leaves the preview as it was.

## Scenario: Asking the assistant reaches PhotoCraft

Asking the assistant "make a 512x512 image with a blue-to-orange gradient" reaches PhotoCraft: the
assistant's turn requests "edit" (Jev suggests it, or PhotoCraft's description points at it), or runs
PhotoCraft's tools itself. Either way the preview becomes a rendered PNG of the gradient, captioned with the
request or "Changed with PhotoCraft's tools.", and the assistant's turn completes rather than running out its
timeout.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "photocraft";
    public const string Version = "1.0.13";

    public static readonly AppManifest Manifest = new(
        Title: "PhotoCraft",
        Description: "Photoshop-style image editor. To make or change an image, request its \"edit\" operation with the person's words; "
            + "its editor drives PhotoCraft's tools, so do not call those tools (doc_new, command_run, ...) yourself.",
        Operations:
        [
            new("open", "Connect PhotoCraft's tools to the brain and prepare the live image editor; it makes no image."),
            new("edit", "Make or change an image from the person's words: a new picture of a size, a gradient, a fill, shapes, text, a filter, a resize. "
                + "Input: the person's request as they said it. "
                + "The request returns at once and PhotoCraft's editor works for up to a minute; then the text {brain}/apps/photocraft/status "
                + "holds its answer and the image {brain}/apps/photocraft/preview the rendered PNG, also shown in PhotoCraft's window. "
                + "Do not wait for it: read the status once (\"Editing: ...\" means the request was taken; \"still working\" means another "
                + "edit is running and this one was not taken, so ask again later), then tell the person PhotoCraft is making it."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg",
        Toolbar: [new("open", Icon: "link", Label: "Connect editor")]);
}
