using System.Text.Json;

namespace IntoChat.OS.Microsoft.Aspire.Deployment;

internal sealed record LiveBuild(string Sha, string Path);

// {Workdir}/live.json names the build the live silos run; absent on first boot, when the AppHost runs from source.
internal static class LiveBuildFile
{
    public static string PathOf(string workdir) => System.IO.Path.Combine(workdir, "live.json");

    public static LiveBuild? Read(string workdir)
    {
        var file = PathOf(workdir);
        return File.Exists(file) ? JsonSerializer.Deserialize<LiveBuild>(File.ReadAllText(file), Json) : null;
    }

    public static void Write(string workdir, string sha)
    {
        var build = System.IO.Path.Combine(workdir, "builds", sha);
        if (!Directory.Exists(build)) { throw new DirectoryNotFoundException($"build {sha} was not published to {build}"); }
        var file = PathOf(workdir);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new LiveBuild(sha, build), Json));
        File.Move(temporary, file, overwrite: true);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
