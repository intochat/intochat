using IntoChat.OS.Modules.Apps;

static class App
{
    public const string Name = "greeter";
    public const string Version = "1.2.0";

    public static readonly AppManifest Manifest = new(
        Title: "Greeter",
        Description: "Greets.",
        Operations: [new("ask", "Greet")],
        Settings: [],
        Slots: []);
}
