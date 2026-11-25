using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Tests.Integration.Ino;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Screens;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Integration;

// Files on a real silo: the shell reads it as the compiled screen app.files/default; its buttons act, and its fields send what the
// person typed, through POST /act.
public sealed class FilesScreenSiloFacts(InoSiloFixture fixture) : IClassFixture<InoSiloFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task The_shell_reads_files_as_a_screen_and_its_buttons_act_through_act()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = fixture.NewBrain("files");
        using var person = fixture.AsPerson(brain);
        var proxies = fixture.Services.GetRequiredService<INeuronProxies>();
        var hosted = fixture.Services.GetService<IHostedAppTypes>();
        var resolver = fixture.Services.GetService<IScreenTargetResolver>();
        var caller = fixture.Edge.Current;

        var (viewportStatus, viewportBody) = await Body(await AppUiEndpoints.ReadNode("viewport", $"{brain}/apps/files", proxies, brain, ct, hosted: hosted, caller: caller, resolver: resolver));
        Assert.Equal(200, viewportStatus);
        Assert.Contains($"{brain}/apps/files/view", viewportBody, StringComparison.Ordinal);

        var first = await Until(brain, view => view.Contains("\"storage-open-0\"", StringComparison.Ordinal), ct);
        Assert.Contains("\"files.run\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\"neuronRef\"", first, StringComparison.Ordinal);

        await Press(brain, "storage-open-0", proxies, hosted, resolver, ct);
        await Press(brain, "view-grid", proxies, hosted, resolver, ct);
        Assert.Contains("Grid view", await View(brain), StringComparison.Ordinal);

        // A new folder with no name asks inline; confirming at the storage's roots (no folder open) is refused by the app and
        // closes the prompt with the reason.
        await Press(brain, "mkdir", proxies, hosted, resolver, ct);
        Assert.Contains("\"prompt-confirm\"", await View(brain), StringComparison.Ordinal);
        await Type(brain, "prompt-input", "reports", proxies, hosted, resolver, ct);
        await Press(brain, "prompt-confirm", proxies, hosted, resolver, ct);
        var refused = await View(brain);
        Assert.DoesNotContain("\"prompt-confirm\"", refused, StringComparison.Ordinal);
        Assert.Contains("destination folder", refused, StringComparison.Ordinal);

        // In a folder, a quick write starts an operation through the runtime's operations gate.
        await Press(brain, "item-open-0", proxies, hosted, resolver, ct);
        await Type(brain, "input-name", "notes.txt", proxies, hosted, resolver, ct);
        await Type(brain, "input-content", "hello", proxies, hosted, resolver, ct);
        await Press(brain, "write", proxies, hosted, resolver, ct);
        var written = await View(brain);
        Assert.Contains("\"title\":\"write\"", written, StringComparison.Ordinal);
    }

    private IScreen Screen(string brain) => SiloNeurons.Get<IScreen>(fixture.Services, new NeuronId(new BrainId(brain), "app.files", "default"));

    private async Task<string> View(string brain) => (await Screen(brain).Read())?.View.GetRawText() ?? "";

    private async Task<string> Until(string brain, Func<string, bool> done, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var view = "";
        while (clock.Elapsed < Within)
        {
            view = await View(brain);
            if (done(view)) { return view; }
            await Task.Delay(50, ct);
        }
        throw new TimeoutException("Files did not reach the expected view: " + view);
    }

    private Task Press(string brain, string key, INeuronProxies proxies, IHostedAppTypes? hosted, IScreenTargetResolver? resolver, CancellationToken ct)
        => Send(brain, key, "command", null, proxies, hosted, resolver, ct);

    // What the client sends after a pause in typing: the field's command with the typed text as its value.
    private Task Type(string brain, string key, string text, INeuronProxies proxies, IHostedAppTypes? hosted, IScreenTargetResolver? resolver, CancellationToken ct)
        => Send(brain, key, "onSubmit", text, proxies, hosted, resolver, ct);

    private async Task Send(string brain, string key, string member, string? value, INeuronProxies proxies, IHostedAppTypes? hosted, IScreenTargetResolver? resolver, CancellationToken ct)
    {
        var view = await View(brain);
        var node = Nodes(JsonDocument.Parse(view).RootElement).FirstOrDefault(n => n.TryGetProperty("key", out var k) && k.GetString() == key && n.TryGetProperty(member, out _));
        Assert.True(node.ValueKind == JsonValueKind.Object, $"The view has no '{key}' with a {member}.");
        var command = node.GetProperty(member);
        var result = await AppUiEndpoints.Act(
            new($"{brain}/apps/files", command.GetProperty("target").GetString(), command.GetProperty("signal").GetString(), command.GetProperty("args").Clone(), value),
            proxies, brain, ct, fixture.Edge.Current, hosted, resolver: resolver);
        var (status, body) = await Body(result);
        Assert.True(status == 200, $"{key}: {status} {body}");
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var child in element.EnumerateObject().SelectMany(p => Nodes(p.Value))) { yield return child; }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().SelectMany(Nodes)) { yield return child; }
        }
    }

    private async Task<(int Status, string Body)> Body(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = fixture.Services };
        using var stream = new MemoryStream();
        http.Response.Body = stream;
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }
}
