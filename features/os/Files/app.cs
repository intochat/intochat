/*
# Files

Browse the connected Windows PC. Opening Files shows the machine's allowed roots; choosing a folder
lists what is inside it, choosing a file shows its text. A C# file is shown as the neuron it is:
its source in a code block, so the program can be read where it lives.

The PC is the brain's `windows` connection, keyed `{brain}/connection/windows/{machineId}`; its file
neuron is `{connection}/files`, confined to the machine's allowed roots. Without a connection the
surface says so and offers nothing to browse. "Open my files" is routed to `open`.

## Scenario: Opening Files lists the roots

Invoking "open" composes the surface, opens a "Files" window and lists the PC's allowed roots.

## Scenario: Browsing a folder lists its entries

Invoking "browse" with a folder path sets the location to that path and shows one button per entry.

## Scenario: Reading a file shows its text

Invoking "read" with a file path shows the file's text; a `.cs` file is shown in a code block.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "files";
    public const string Version = "1.0.0";

    public static readonly AppManifest Manifest = new(
        Title: "Files",
        Description: "Browse the folders and files of the connected Windows PC.",
        Operations:
        [
            new("open", "Open Files on the PC's allowed roots; input \"docked\" skips the window."),
            new("browse", "List the folder at the input path (empty input lists the allowed roots)."),
            new("read", "Show the text of the file at the input path; a .cs file is shown as C# source."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg",
        Toolbar:
        [
            new("open", Icon: "folder", Label: "Roots"),
        ]);
}
