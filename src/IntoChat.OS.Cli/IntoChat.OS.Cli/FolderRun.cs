using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace IntoChat.OS.Cli;

internal static class FolderRun
{
    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    internal static async Task<int> Execute(AppCommand command, TextWriter error, TextWriter output, Func<DevAppPush, CancellationToken, Task<DevAppResult>> push, CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(command.Target!);
        if (!File.Exists(Path.Combine(folder, "app.cs")))
        {
            error.WriteLine("The folder needs app.cs.");
            return 1;
        }
        var tokenPath = DevTokenPath();
        if (!File.Exists(tokenPath))
        {
            error.WriteLine("start the local OS first");
            return 1;
        }
        var secret = (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim();
        if (secret.Length == 0)
        {
            error.WriteLine("start the local OS first");
            return 1;
        }
        var name = AppName(await File.ReadAllTextAsync(Path.Combine(folder, "app.cs"), cancellationToken)) ?? Path.GetFileName(folder).ToLowerInvariant();
        async Task<DevAppResult> Send(CancellationToken ct)
        {
            var body = new DevAppPush([.. Read(folder)]);
            return await push(body, ct);
        }
        try
        {
            var first = await Send(cancellationToken);
            Write(first, error, output, name);
        }
        catch (HttpRequestException)
        {
            error.WriteLine($"No local OS answers at {command.Endpoint ?? DefaultEndpoint}.");
            return 1;
        }
        var pulses = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            InternalBufferSize = 16 * 1024,
        };
        void Signal(object sender, FileSystemEventArgs args)
        {
            if (AppWatch.Includes(args.FullPath)) { pulses.Writer.TryWrite(1); }
        }
        watcher.Changed += Signal;
        watcher.Created += Signal;
        watcher.Deleted += Signal;
        watcher.Renamed += Signal;
        watcher.Error += (_, _) => pulses.Writer.TryWrite(1);
        watcher.EnableRaisingEvents = true;
        await Coalesce(pulses.Reader, Quiet, async () =>
        {
            try
            {
                var result = await Send(cancellationToken);
                Write(result, error, output, name);
            }
            catch (HttpRequestException) { error.WriteLine($"No local OS answers at {command.Endpoint ?? DefaultEndpoint}."); }
        }, cancellationToken);
        return 0;
    }

    internal static async Task Coalesce(ChannelReader<byte> pulses, TimeSpan quiet, Func<Task> push, CancellationToken cancellationToken)
    {
        while (await pulses.WaitToReadAsync(cancellationToken))
        {
            while (pulses.TryRead(out _)) { }
            try { await Task.Delay(quiet, cancellationToken); }
            catch (OperationCanceledException) { break; }
            while (pulses.TryRead(out _)) { }
            await push();
        }
    }

    internal static Uri DefaultEndpoint { get; } = new("http://127.0.0.1:5000");

    internal static async Task<DevAppResult> Post(Uri endpoint, string name, string secret, DevAppPush push, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Put, "dev/apps/" + Uri.EscapeDataString(name))
        {
            Content = new StringContent(JsonSerializer.Serialize(push, CliJsonContext.Default.DevAppPush), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        { throw new HttpRequestException($"The local OS answered {(int)response.StatusCode}."); }
        return JsonSerializer.Deserialize(text, CliJsonContext.Default.DevAppResult) ?? new DevAppResult([]);
    }

    internal static string DevTokenPath()
    {
        var home = Environment.GetEnvironmentVariable("IntoChatOS__Home");
        if (string.IsNullOrWhiteSpace(home)) { home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".intochat"); }
        return Path.Combine(home, "dev-token");
    }

    internal static string? AppName(string appCs)
    {
        const string marker = "const string Name = \"";
        var at = appCs.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) { return null; }
        var start = at + marker.Length;
        var end = appCs.IndexOf('"', start);
        return end > start ? appCs[start..end] : null;
    }

    private static IEnumerable<DevAppFile> Read(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!AppWatch.Includes(path)) { continue; }
            var extension = Path.GetExtension(path);
            if (extension is not (".cs" or ".md")) { continue; }
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            if (relative.Split('/').Contains("..")) { continue; }
            yield return new DevAppFile(relative, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }

    private static void Write(DevAppResult result, TextWriter error, TextWriter output, string name)
    {
        if (result.Diagnostics.Length == 0) { output.WriteLine($"Reloaded {name}."); return; }
        foreach (var diagnostic in result.Diagnostics)
        { error.WriteLine($"{diagnostic.File}({diagnostic.Line}): {diagnostic.Id} {diagnostic.Message}"); }
    }
}
