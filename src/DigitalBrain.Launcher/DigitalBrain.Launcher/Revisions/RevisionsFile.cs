using System.Text.Json;

namespace DigitalBrain.Launcher.Revisions;

public static class RevisionsFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static RevisionsDocument Read(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<RevisionsDocument>(File.ReadAllText(path), Json) ?? new(null, []) : new(null, []);

    // Written whole, through a temporary file, so a crash mid-write never leaves half a document.
    public static void Write(string path, RevisionsDocument document)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
